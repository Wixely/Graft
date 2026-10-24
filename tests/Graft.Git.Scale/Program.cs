using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Graft.Git;

if (args.Length > 1 || args.Length == 1 && args[0] != "--full") return 2;
if (SelfChecks.Run() != 0) return 1;
using (OffsetStream offset = new())
{
    GitObject blob = new(3, [1, 2, 3]);
    long start = offset.Position;
    Pack.Write(offset, [blob]);
    offset.Position = start;
    Check(Pack.Read(offset, new Dictionary<string, GitObject>()).Objects.ContainsKey(blob.Id),
        "pack offsets beyond signed 32-bit range round trip");
}
bool full = args is ["--full"];
long targetBytes = full ? Limits.ExpandedBytes : 96L * 1024 * 1024;
using TemporaryStorage scratch = new();
try
{
    using FileStream pack = scratch.CreateFile();
    int blobCount = (int)((targetBytes + Limits.ObjectBytes - 1) / Limits.ObjectBytes);
    int treeSize = Enumerable.Range(0, blobCount).Sum(index => Encoding.ASCII.GetByteCount($"100644 file-{index:D3}\0") + 20);
    string CommitText(string treeId) => $"tree {treeId}\nauthor Scale <scale@example.invalid> 1000000000 +0000\ncommitter Scale <scale@example.invalid> 1000000000 +0000\n\nScale fixture\n";
    int commitSize = Encoding.ASCII.GetByteCount(CommitText(Limits.Zero));
    pack.Write("PACK"u8);
    byte[] number = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(number, 2);
    pack.Write(number);
    BinaryPrimitives.WriteUInt32BigEndian(number, (uint)blobCount + 2);
    pack.Write(number);
    using MemoryStream treeData = new();
    long remaining = targetBytes - treeSize - commitSize;
    for (int index = 0; index < blobCount; index++)
    {
        int length = (int)Math.Min(Limits.ObjectBytes, remaining);
        byte[] content = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(content, index);
        GitObject blob = new(3, content);
        treeData.Write(Encoding.ASCII.GetBytes($"100644 file-{index:D3}\0"));
        treeData.Write(Convert.FromHexString(blob.Id));
        Pack.WriteHeader(pack, 3, length);
        Pack.Compress(pack, content);
        remaining -= length;
    }
    GitObject tree = new(2, treeData.ToArray());
    GitObject commit = new(1, Encoding.ASCII.GetBytes(CommitText(tree.Id)));
    foreach (GitObject item in new[] { tree, commit })
    {
        Pack.WriteHeader(pack, item.Type, item.Size);
        Pack.Compress(pack, item.Data);
    }
    pack.Position = 0;
    byte[] checksum = SHA1.HashData(pack);
    pack.Write(checksum);
    pack.Position = 0;
    using FileStream request = scratch.CreateFile();
    Packet.Write(request, $"{Limits.Zero} {commit.Id} refs/heads/main\0report-status\n");
    Packet.Flush(request);
    pack.CopyTo(request);
    request.Position = 0;
    string repositoryPath = Path.Combine(scratch.DirectoryPath, "repository");
    Repository repository = new(new DurableRepositoryStorage(repositoryPath));
    Stopwatch elapsed = Stopwatch.StartNew();
    repository.Receive(request);
    Check(repository.Objects.Values.Sum(obj => (long)obj.Size) == targetBytes, "exact repository byte budget accepted");
    Check(repository.Refs["refs/heads/main"] == commit.Id, "push publishes expected commit");
    Console.WriteLine($"Receive: {elapsed.Elapsed}; expanded bytes: {targetBytes}");
    repository = new(new DurableRepositoryStorage(repositoryPath));
    Check(repository.Objects.Values.Sum(obj => (long)obj.Size) == targetBytes, "restart preserves all object sizes");
    Check(repository.Refs["refs/heads/main"] == commit.Id, "restart preserves ref");
    if (full)
    {
        using MemoryStream oversized = new();
        Packet.Write(oversized, $"{commit.Id} {commit.Id} refs/heads/main\0report-status\n");
        Packet.Flush(oversized);
        oversized.Write(Pack.Write([new GitObject(3, [255])]));
        oversized.Position = 0;
        try { repository.Receive(oversized); throw new Exception("accepted 4 GiB plus one byte"); }
        catch (GitInputException error) when (error.Message == "repository-budget") { }
        Check(new Repository(new DurableRepositoryStorage(repositoryPath)).Objects.Count == blobCount + 2,
            "over-budget push leaves durable objects and refs unchanged");
    }
    using MemoryStream wants = new();
    Packet.Write(wants, $"want {commit.Id}\n");
    Packet.Flush(wants);
    Packet.Write(wants, "done\n");
    wants.Position = 0;
    using FileStream response = scratch.CreateFile();
    repository.Upload(wants, response);
    response.Position = 0;
    Check(new Cursor(response, 0, response.Length).Packet() == "NAK\n", "fetch protocol prefix");
    string gitDirectory = Path.Combine(scratch.DirectoryPath, "git");
    Directory.CreateDirectory(gitDirectory);
    await Git(["init", "--bare", gitDirectory]);
    await Git(["-C", gitDirectory, "index-pack", "--stdin", "--strict"], response);
    await Git(["-C", gitDirectory, "update-ref", "refs/heads/main", commit.Id]);
    await Git(["-C", gitDirectory, "fsck", "--full", "--strict"]);
    Check(true, "real Git validates streamed fetch and object graph");
    Console.WriteLine($"PASS: scale workflow; elapsed {elapsed.Elapsed}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64}");
    return 0;
}
finally
{
    foreach (string path in Directory.EnumerateFiles(scratch.DirectoryPath, "*", SearchOption.AllDirectories))
        File.SetAttributes(path, FileAttributes.Normal);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS: " + name);
}

static async Task Git(string[] arguments, Stream? input = null)
{
    ProcessStartInfo start = new("git")
    {
        UseShellExecute = false,
        RedirectStandardInput = input is not null,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
    using Process process = Process.Start(start)!;
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    if (input is not null)
    {
        await input.CopyToAsync(process.StandardInput.BaseStream);
        process.StandardInput.Close();
    }
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(await error);
    await output;
}

sealed class OffsetStream : Stream
{
    private const long Origin = 3L * 1024 * 1024 * 1024;
    private readonly MemoryStream content = new();
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => true;
    public override long Length => Origin + content.Length;
    public override long Position { get => Origin + content.Position; set => content.Position = value - Origin; }
    public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => content.Read(buffer);
    public override void Write(byte[] buffer, int offset, int count) => content.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => content.Write(buffer);
    public override void Flush() => content.Flush();
    public override long Seek(long offset, SeekOrigin origin) =>
        Origin + content.Seek(origin == SeekOrigin.Begin ? offset - Origin : offset, origin);
    public override void SetLength(long value) => content.SetLength(value - Origin);
    protected override void Dispose(bool disposing)
    {
        if (disposing) content.Dispose();
        base.Dispose(disposing);
    }
}
