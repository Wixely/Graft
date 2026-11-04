using System.Text;
using Graft.Git;

namespace Graft.Remote;

/// <summary>
/// The remote helper Git runs for an address written <c>graft::https://...</c> (ADR 0043). It offers Git one capability,
/// <c>connect</c>, so Git speaks its own protocol through this program exactly as it would through SSH, and this program
/// carries each request to the forge over HTTPS: a fetch in protocol version 2, whose requests are each answered on their
/// own, and a push in version 0, which is one request. Anything large goes as a chunked transfer, so no single request
/// is bigger than the forge's part size and no answer has to start within a proxy's timeout.
/// </summary>
internal sealed class GitBridge(string remote, Uri address, Stream input, Stream output)
{
    private bool progress;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0) return 0;
            if (line == "capabilities") await WriteAsync("connect\noption\n\n", cancellationToken);
            else if (line.StartsWith("option ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', 3);
                if (parts.Length == 3 && parts[1] == "progress") progress = parts[2] == "true";
                await WriteAsync(parts.Length == 3 && parts[1] is "progress" or "verbosity" ? "ok\n" : "unsupported\n", cancellationToken);
            }
            else if (line.StartsWith("connect ", StringComparison.Ordinal))
            {
                string service = line[8..];
                if (service is not ("git-upload-pack" or "git-receive-pack"))
                {
                    await WriteAsync("fallback\n", cancellationToken);
                    continue;
                }
                PinLargeFileAddress();
                using ForgeClient forge = new(address, null, Report);
                await WriteAsync("\n", cancellationToken);
                if (service == "git-upload-pack") await FetchAsync(forge, cancellationToken);
                else await PushAsync(forge, cancellationToken);
                return 0;
            }
            else throw new HelperException("git asked for '" + line + "', which git-remote-graft does not do");
        }
    }

    private void Report(string message)
    {
        if (progress) Console.Error.WriteLine(message);
    }

    // ---------------------------------------------------------------------------------------------------
    // Fetch: protocol version 2, a request at a time.
    // ---------------------------------------------------------------------------------------------------

    private async Task FetchAsync(ForgeClient forge, CancellationToken cancellationToken)
    {
        byte[] advertisement;
        using (HttpResponseMessage response = await forge.SendAsync(() =>
        {
            HttpRequestMessage request = new(HttpMethod.Get, forge.Under("info/refs?service=git-upload-pack"));
            request.Headers.Add("Git-Protocol", "version=2");
            return request;
        }, retry: true, cancellationToken))
            advertisement = await ForgeClient.ReadSuccessAsync(response, "reading the repository's references", cancellationToken);
        advertisement = WithoutServiceHeader(advertisement);
        if (!Encoding.ASCII.GetString(advertisement, 0, Math.Min(advertisement.Length, 18)).Contains("version 2", StringComparison.Ordinal))
            throw new HelperException("'" + address + "' does not speak Git protocol version 2; git-remote-graft needs a Graft forge");
        await output.WriteAsync(advertisement, cancellationToken);
        await output.FlushAsync(cancellationToken);

        while (true)
        {
            byte[]? request = await ReadSectionAsync(input, 16 * 1024 * 1024, cancellationToken);
            // Git ends the conversation by closing the pipe, or with a flush on its own.
            if (request is null || request.Length == 4) return;
            string command = FirstPacket(request) ?? "";
            if (command == "command=fetch")
            {
                // The fetch itself is the request whose answer may be a large pack and may take the forge a while to
                // build, so it goes as a transfer: the answer is read back in ranges once it is ready.
                using MemoryStream body = new(request, writable: false);
                await using FileStream answer = TemporaryFile();
                await forge.TransferAsync(forge.Under("graft/transfers?kind=git-upload-pack&protocol=2&size=" + request.Length),
                    body, answer, "fetch", cancellationToken);
                answer.Position = 0;
                await answer.CopyToAsync(output, cancellationToken);
            }
            else
            {
                using HttpResponseMessage response = await forge.SendAsync(() =>
                {
                    HttpRequestMessage post = new(HttpMethod.Post, forge.Under("git-upload-pack")) { Content = new ByteArrayContent(request) };
                    post.Content.Headers.ContentType = new("application/x-git-upload-pack-request");
                    post.Headers.Add("Git-Protocol", "version=2");
                    post.Headers.Accept.ParseAdd("application/x-git-upload-pack-result");
                    return post;
                }, retry: true, cancellationToken);
                await output.WriteAsync(await ForgeClient.ReadSuccessAsync(response, command, cancellationToken), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Push: protocol version 0, one request carried as a transfer.
    // ---------------------------------------------------------------------------------------------------

    private async Task PushAsync(ForgeClient forge, CancellationToken cancellationToken)
    {
        byte[] advertisement;
        using (HttpResponseMessage response = await forge.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, forge.Under("info/refs?service=git-receive-pack")), retry: true, cancellationToken))
            advertisement = await ForgeClient.ReadSuccessAsync(response, "reading the repository's references", cancellationToken);
        await output.WriteAsync(WithoutServiceHeader(advertisement), cancellationToken);
        await output.FlushAsync(cancellationToken);

        byte[]? commands = await ReadSectionAsync(input, 16 * 1024 * 1024, cancellationToken);
        // Nothing to push: Git sends a flush and goes away.
        if (commands is null || commands.Length <= 4) return;

        // A pack follows unless every command is a deletion. Where it ends is found by walking it, because Git writes it
        // and then waits for the answer, with nothing to mark the end but the pack's own structure.
        bool expectPack = false;
        foreach (string line in Packets(commands))
        {
            string[] parts = line.Split('\0')[0].TrimEnd('\n').Split(' ');
            if (parts.Length == 3 && parts[1] != Limits.Zero) expectPack = true;
        }
        await using FileStream request = TemporaryFile();
        await request.WriteAsync(commands, cancellationToken);
        if (expectPack)
        {
            // Buffered, because the walk reads a byte at a time and a pipe read per byte is slow. Reading ahead cannot block:
            // Git sends nothing after the pack until it has the answer, and the walk asks only for bytes the pack still owes.
            try { Pack.Capture(new BufferedStream(input, 64 * 1024), request, cancellationToken); }
            catch (GitInputException error) { throw new HelperException("the pack Git wrote is beyond what Graft accepts (" + error.Message + ")"); }
        }
        await request.FlushAsync(cancellationToken);
        request.Position = 0;

        await using FileStream answer = TemporaryFile();
        await forge.TransferAsync(forge.Under("graft/transfers?kind=git-receive-pack&size=" + request.Length), request, answer, "push", cancellationToken);
        answer.Position = 0;
        await answer.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------
    // Git LFS beside it.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Records the remote's Git LFS address as the plain https one. git-lfs works its address out from the remote's URL
    /// after Git's <c>insteadOf</c> rewriting, which here turns it into <c>graft::https://...</c>, an address git-lfs cannot
    /// use. A remote's own <c>lfsurl</c> comes first for git-lfs, so this sets it, and keeps it in step with the remote's
    /// URL for as long as it is the one this program set, marked by <c>graftlfsurl</c>. git-lfs rewrites that address too,
    /// by the longest matching <c>insteadOf</c>, so a rule mapping exactly this address to itself, in the repository's own
    /// configuration, keeps it plain https. No Git remote begins with <c>.../info/lfs</c>, so Git never applies that rule.
    /// </summary>
    private void PinLargeFileAddress()
    {
        if (Environment.GetEnvironmentVariable("GIT_DIR") is not { Length: > 0 }) return;
        if (remote.Contains("://", StringComparison.Ordinal) || remote.Contains("::", StringComparison.Ordinal)) return;
        try
        {
            if (Git.Config("--get", "remote." + remote + ".url") is null)
            {
                Console.Error.WriteLine("graft: did not record the Git LFS address: this repository has no remote '" + remote + "' configured");
                return;
            }
            string wanted = address.AbsoluteUri.TrimEnd('/');
            wanted += wanted.EndsWith(".git", StringComparison.Ordinal) ? "/info/lfs" : ".git/info/lfs";
            string? current = Git.Config("--get", "remote." + remote + ".lfsurl");
            string? ours = Git.Config("--get", "remote." + remote + ".graftlfsurl");
            if (current is not null && current != wanted && ours != "true") return;
            if (current != wanted)
            {
                Git.SetConfig("remote." + remote + ".lfsurl", wanted);
                Git.SetConfig("remote." + remote + ".graftlfsurl", "true");
            }
            if (Git.Config("--get", "url." + wanted + ".insteadOf") != wanted) Git.SetConfig("url." + wanted + ".insteadOf", wanted);
            // Read back, so a write Git accepted but that did not land is reported rather than found by git-lfs later.
            if (Git.Config("--get", "remote." + remote + ".lfsurl") != wanted || Git.Config("--get", "url." + wanted + ".insteadOf") != wanted)
                throw new HelperException("the address did not read back as written");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine("graft: could not record the Git LFS address for '" + remote + "', so Git LFS may try a graft:: address: " + error.Message);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Plumbing.
    // ---------------------------------------------------------------------------------------------------

    private static FileStream TemporaryFile() => new(Path.Combine(Path.GetTempPath(), "git-remote-graft-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);

    /// <summary>Drops the <c># service=...</c> line and its flush that smart HTTP puts in front of an advertisement.</summary>
    private static byte[] WithoutServiceHeader(byte[] advertisement)
    {
        if (advertisement.Length < 8 || Encoding.ASCII.GetString(advertisement, 4, 4) != "# se") return advertisement;
        int length = Convert.ToInt32(Encoding.ASCII.GetString(advertisement, 0, 4), 16);
        int next = length;
        if (advertisement.Length >= next + 4 && Encoding.ASCII.GetString(advertisement, next, 4) == "0000") next += 4;
        return advertisement[next..];
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        List<byte> line = [];
        byte[] one = new byte[1];
        while (true)
        {
            int read = await input.ReadAsync(one, cancellationToken);
            if (read == 0) return line.Count == 0 ? null : Encoding.UTF8.GetString([.. line]);
            if (one[0] == '\n') return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
            line.Add(one[0]);
        }
    }

    private async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Packet lines up to and including the next flush, as raw bytes, or null at a clean end of input. A delimiter or a
    /// response-end packet is part of the section.
    /// </summary>
    internal static async Task<byte[]?> ReadSectionAsync(Stream source, int limit, CancellationToken cancellationToken)
    {
        MemoryStream section = new();
        byte[] header = new byte[4];
        while (true)
        {
            int got = 0;
            while (got < 4)
            {
                int read = await source.ReadAsync(header.AsMemory(got, 4 - got), cancellationToken);
                if (read == 0)
                {
                    if (got == 0 && section.Length == 0) return null;
                    throw new HelperException("git's request ended part way through");
                }
                got += read;
            }
            section.Write(header);
            if (!int.TryParse(Encoding.ASCII.GetString(header), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out int length))
                throw new HelperException("git sent something that is not a packet line");
            if (length is 0) return section.ToArray();
            if (length is 1 or 2) continue;
            if (length is < 4 or > 65520 || section.Length + length > limit) throw new HelperException("git's request is larger than expected");
            byte[] payload = new byte[length - 4];
            await source.ReadExactlyAsync(payload, cancellationToken);
            section.Write(payload);
        }
    }

    private static string? FirstPacket(byte[] section) => Packets(section).FirstOrDefault()?.TrimEnd('\n');

    private static IEnumerable<string> Packets(byte[] section)
    {
        int position = 0;
        while (position + 4 <= section.Length)
        {
            int length = Convert.ToInt32(Encoding.ASCII.GetString(section, position, 4), 16);
            if (length is 0 or 1 or 2) { position += 4; continue; }
            yield return Encoding.UTF8.GetString(section, position + 4, length - 4);
            position += length;
        }
    }
}
