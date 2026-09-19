using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Graft.ManagedGit;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Graft.ManagedGit.Client <server DLL or native executable>");
    return 2;
}
var serverPath = Path.GetFullPath(args[0]);
var folder = Path.Combine(Path.GetTempPath(), "graft-managed-git-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var config = Path.Combine(folder, "empty.gitconfig"); File.WriteAllText(config, "");
var lines = new ConcurrentQueue<string>();
var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
Process? server = null;
Task? reader = null;
Task<string>? errors = null;
try
{
    Console.WriteLine("CLIENT: " + (await Git(folder, ["--version"])).Trim());
    var self = ServerStart("--self-test");
    var check = await Run(self);
    Assert(check.ExitCode == 0, "server self checks");
    Console.WriteLine(Encoding.UTF8.GetString(check.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim());
    server = Process.Start(ServerStart())!;
    var activeServer = server;
    errors = activeServer.StandardError.ReadToEndAsync();
    reader = Task.Run(async () =>
    {
        while (await activeServer.StandardOutput.ReadLineAsync() is string line)
        {
            lines.Enqueue(line);
            if (line.StartsWith("READY: ", StringComparison.Ordinal)) ready.TrySetResult(line[7..]);
        }
    });
    var address = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    var url = address + "/fixture.git";
    Console.WriteLine(lines.First(l => l.StartsWith("MODE:", StringComparison.Ordinal)));
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(25) };
    http.BaseAddress = new Uri(address);

    await Git(folder, ["clone", "--no-checkout", url, "empty-clone"]);
    Console.WriteLine("PASS: empty clone with default protocol negotiation");
    var source = Path.Combine(folder, "source"); Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    var random = new Random(514);
    var content = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"row {i:D4} {random.NextInt64():x16} synthetic content")) + "\n";
    File.WriteAllText(Path.Combine(source, "data.txt"), content);
    for (var i = 0; i < 12; i++) File.WriteAllText(Path.Combine(source, $"similar-{i:D2}.txt"), content + $"variant {i}\n");
    File.WriteAllBytes(Path.Combine(source, "binary.dat"), Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Synthetic initial commit"]);
    for (var i = 0; i < 5; i++)
    {
        File.AppendAllText(Path.Combine(source, "data.txt"), $"revision {i}\n");
        await Git(source, ["add", "data.txt"]);
        await Git(source, ["commit", "-m", $"Synthetic revision {i}"]);
    }
    var gitlink = (await Git(source, ["rev-parse", "HEAD"])).Trim();
    var linkBlob = (await Git(source, ["hash-object", "-w", "--stdin"], "data.txt")).Trim();
    await Git(source, ["update-index", "--add", "--cacheinfo", "120000," + linkBlob + ",link"]);
    await Git(source, ["update-index", "--add", "--cacheinfo", "160000," + gitlink + ",submodule"]);
    await Git(source, ["commit", "-m", "Store symlink and gitlink without server checkout"]);
    await Git(source, ["branch", "feature/topic"]);
    await Git(source, ["tag", "-a", "v0.1", "-m", "Synthetic annotated tag"]);
    await Git(source, ["remote", "add", "origin", url]);
    await Git(source, ["push", "origin", "--all"]);
    await Git(source, ["push", "origin", "--tags"]);
    await Git(folder, ["clone", "--no-checkout", url, "clone"]);
    var clone = Path.Combine(folder, "clone");
    await Verify(source, clone, url);
    Console.WriteLine("PASS: initial push, branches, annotated tag, clone, all object IDs, fsck");

    File.AppendAllText(Path.Combine(source, "data.txt"), "incremental change using an existing delta base\n");
    await Git(source, ["add", "data.txt"]);
    await Git(source, ["commit", "-m", "Synthetic incremental update"]);
    await Git(source, ["tag", "-a", "v0.2", "-m", "Second synthetic tag"]);
    await Git(source, ["push", "origin", "main", "--tags"]);
    await Git(clone, ["fetch", "origin", "--tags"]);
    await Verify(source, clone, url);
    Console.WriteLine("PASS: second push and fetch, preserved IDs, fsck");

    var head = (await Git(source, ["rev-parse", "HEAD"])).Trim();
    // Use Git itself to generate both pack delta encodings, independent of our writer.
    foreach (var ofs in new[] { false, true })
    {
        var options = new List<string> { "pack-objects", "--stdout", "--revs" };
        if (ofs) options.Add("--delta-base-offset");
        var pack = await GitBytes(source, options.ToArray(), "HEAD\n");
        var inspected = Pack.Read(pack, new Dictionary<string, GitObject>());
        Assert(ofs ? inspected.OffsetDeltas > 0 : inspected.ReferenceDeltas > 0, "Git-generated delta fixture");
        var response = await Post("git-receive-pack", SelfChecks.Push([(Limits.Zero, head, ofs ? "refs/heads/ofs-proof" : "refs/heads/ref-proof")], pack));
        Assert(response == HttpStatusCode.OK, "server delta receive");
        Console.WriteLine("PASS: Git-generated " + (ofs ? "OFS" : "REF") + " delta pack accepted by server");
    }
    var before = await Git(source, ["ls-remote", "origin"]);
    var validPack = Pack.Write([]); var corrupt = (byte[])validPack.Clone(); corrupt[^1] ^= 1;
    await RejectHttp("pack checksum", SelfChecks.Push([(head, head, "refs/heads/main")], corrupt));
    await RejectHttp("stale ref", SelfChecks.Push([(Limits.Zero, head, "refs/heads/main")], validPack));
    await RejectHttp("traversal", SelfChecks.Push([(Limits.Zero, head, "refs/heads/../escape")], validPack));
    await RejectHttp("truncated packet", "0008ab"u8.ToArray());
    await RejectHttp("oversized object", SelfChecks.Push([(head, head, "refs/heads/main")], SelfChecks.FixturePack([(3, new byte[Limits.ObjectBytes + 1], null)])));
    var blob = new GitObject(3, "not a commit"u8.ToArray());
    await RejectHttp("invalid mixed batch", SelfChecks.Push([(Limits.Zero, head, "refs/heads/should-not-exist"), (Limits.Zero, blob.Id, "refs/heads/bad")], Pack.Write([blob])));
    Assert(before == await Git(source, ["ls-remote", "origin"]), "refs unchanged after rejected requests");
    await Git(clone, ["fetch", "origin", "--tags"]);
    await Git(clone, ["fsck", "--full", "--strict"]);
    Console.WriteLine("PASS: rejected HTTP inputs preserve refs; subsequent fetch and fsck succeed");
    Console.WriteLine(lines.Last(l => l.StartsWith("RECEIVE", StringComparison.Ordinal)));
    Console.WriteLine("ACCEPTANCE: PASS; loopback HTTP, in-memory repository only");
    return 0;

    async Task<HttpStatusCode> Post(string service, byte[] payload)
    {
        using var request = new ByteArrayContent(payload);
        request.Headers.ContentType = new("application/x-" + service + "-request");
        using var response = await http.PostAsync("/fixture.git/" + service, request);
        return response.StatusCode;
    }
    async Task RejectHttp(string name, byte[] payload)
    {
        Assert(await Post("git-receive-pack", payload) == HttpStatusCode.BadRequest, "HTTP rejection " + name);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("ACCEPTANCE: FAIL: " + ex.GetType().Name + ": " + ex.Message);
    return 1;
}
finally
{
    if (server is not null)
    {
        if (!server.HasExited) server.Kill(entireProcessTree: true);
        await server.WaitForExitAsync();
        if (reader is not null) await reader;
        if (errors is not null) await errors;
        server.Dispose();
    }
    // Only the unique temporary directory allocated above is eligible for cleanup.
    var root = Path.GetFullPath(folder);
    var temp = Path.GetFullPath(Path.GetTempPath());
    if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("graft-managed-git-", StringComparison.Ordinal))
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { Console.Error.WriteLine("WARN: temporary fixture cleanup failed"); }
    }
}

ProcessStartInfo ServerStart(params string[] arguments)
{
    var dll = serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    var info = new ProcessStartInfo(dll ? "dotnet" : serverPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
    if (dll) info.ArgumentList.Add(serverPath);
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return info;
}

async Task<string> Git(string directory, string[] arguments, string? input = null) => Encoding.UTF8.GetString(await GitBytes(directory, arguments, input));

async Task<byte[]> GitBytes(string directory, string[] arguments, string? input = null)
{
    var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
    info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    info.Environment["GIT_CONFIG_GLOBAL"] = config;
    info.Environment["GIT_TERMINAL_PROMPT"] = "0";
    info.Environment["GIT_AUTHOR_DATE"] = "2023-11-14T22:13:20Z";
    info.Environment["GIT_COMMITTER_DATE"] = "2023-11-14T22:13:20Z";
    foreach (var value in new[] { "user.name=Fixture", "user.email=fixture@example.invalid", "commit.gpgSign=false", "tag.gpgSign=false", "credential.helper=", "core.autocrlf=false", "http.proxy=", "protocol.file.allow=never" })
    {
        info.ArgumentList.Add("-c"); info.ArgumentList.Add(value);
    }
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    var result = await Run(info, input);
    if (result.ExitCode != 0) throw new InvalidOperationException("Git " + arguments[0] + " failed: " + result.Error);
    return result.Output;
}

async Task Verify(string source, string clone, string url)
{
    var expected = (await Git(source, ["rev-list", "--objects", "--all", "--no-object-names"])).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
    var actual = (await Git(clone, ["rev-list", "--objects", "--all", "--no-object-names"])).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
    Assert(expected.SequenceEqual(actual), "all reachable object IDs");
    var refs = (await Git(source, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads", "refs/tags"])).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var remote = await Git(source, ["ls-remote", url]);
    foreach (var reference in refs)
    {
        var parts = reference.Trim().Split(' ');
        Assert(remote.Contains(parts[0] + "\t" + parts[1], StringComparison.Ordinal), "advertised ref IDs");
    }
    await Git(clone, ["fsck", "--full", "--strict"]);
}

static async Task<(int ExitCode, byte[] Output, string Error)> Run(ProcessStartInfo info, string? input = null)
{
    using var process = Process.Start(info)!;
    using var output = new MemoryStream();
    var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
    var error = process.StandardError.ReadToEndAsync();
    try
    {
        if (info.RedirectStandardInput)
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await copy;
        return (process.ExitCode, output.ToArray(), await error);
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

static void Assert(bool value, string name)
{
    if (!value) throw new InvalidOperationException("Assertion failed: " + name);
}
