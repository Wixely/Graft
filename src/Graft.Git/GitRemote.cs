using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Graft.Git;

/// <summary>What a remote repository offers: its branches and tags, the branch its HEAD names, and how to ask for them.</summary>
public sealed record RemoteAdvertisement(
    IReadOnlyDictionary<string, string> Refs,
    string? HeadBranch,
    int ProtocolVersion,
    Uri Address,
    IReadOnlySet<string> Capabilities);

/// <summary>Why talking to a remote failed. <see cref="Unavailable"/> is the one that means the repository may be gone.</summary>
public enum RemoteFailure
{
    /// <summary>The server answered that there is nothing here for an anonymous reader: 401, 403, 404, 410 or 451.</summary>
    Unavailable,
    /// <summary>The server could not be reached or failed: a connection error, a timeout, 429 or a 5xx.</summary>
    Unreachable,
    /// <summary>The server answered with something that is not Git's smart HTTP protocol, or a protocol error.</summary>
    Protocol,
    /// <summary>The repository is larger than this engine will hold.</summary>
    TooLarge
}

public sealed class GitRemoteException(RemoteFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public RemoteFailure Failure { get; } = failure;
}

/// <summary>
/// The client half of Git's smart HTTP protocol, for fetching: it lists a remote repository's branches and tags and
/// fetches the objects this repository lacks as one pack. Protocol version 2 is asked for and used when offered;
/// otherwise version 0. Only the anonymous, stateless form is spoken, and only SHA-1 repositories are accepted.
///
/// <para>
/// The caller supplies the <see cref="HttpClient"/>, and with it the decision of which addresses may be reached. Nothing
/// here resolves a host or opens a socket of its own.
/// </para>
/// </summary>
public sealed class GitRemote(HttpClient client)
{
    private const string Agent = "agent=graft";
    private const int MaximumAdvertisementBytes = 64 * 1024 * 1024;
    private static readonly string[] Prefixes = ["refs/heads/", "refs/tags/"];

    /// <summary>Lists the branches and tags of the repository at <paramref name="repository"/>.</summary>
    public async Task<RemoteAdvertisement> ListAsync(Uri repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string root = repository.GetLeftPart(UriPartial.Path).TrimEnd('/');
        using HttpRequestMessage request = new(HttpMethod.Get, root + "/info/refs?service=git-upload-pack");
        request.Headers.Add("Git-Protocol", "version=2");
        using HttpResponseMessage response = await Send(request, cancellationToken);
        Require(response, "application/x-git-upload-pack-advertisement");
        // A host that moved the repository redirects the first request; everything after it goes where it ended up.
        Uri final = response.RequestMessage?.RequestUri ?? request.RequestUri!;
        string finalPath = final.GetLeftPart(UriPartial.Path);
        Uri address = new(finalPath.EndsWith("/info/refs", StringComparison.Ordinal) ? finalPath[..^"/info/refs".Length] : root);

        await using Stream body = await Read(response, cancellationToken);
        PacketReader reader = new(body, MaximumAdvertisementBytes);
        Frame first = await reader.NextAsync(cancellationToken);
        if (first.Kind == FrameKind.Data && first.Text.StartsWith("# service=", StringComparison.Ordinal))
        {
            if (first.Text.TrimEnd('\n') != "# service=git-upload-pack") throw Protocol("the server offered another service");
            Frame flush = await reader.NextAsync(cancellationToken);
            if (flush.Kind != FrameKind.Flush) throw Protocol("the service line was not followed by a flush");
            first = await reader.NextAsync(cancellationToken);
        }
        if (first.Kind == FrameKind.Data && first.Text.TrimEnd('\n') == "version 2")
            return await ListV2Async(address, reader, cancellationToken);
        return await ListV0Async(address, reader, first, cancellationToken);
    }

    private async Task<RemoteAdvertisement> ListV2Async(Uri address, PacketReader reader, CancellationToken cancellationToken)
    {
        HashSet<string> capabilities = new(StringComparer.Ordinal);
        for (Frame line = await reader.NextAsync(cancellationToken); line.Kind != FrameKind.Flush; line = await reader.NextAsync(cancellationToken))
        {
            if (line.Kind != FrameKind.Data) throw Protocol("a capability advertisement held a non-data packet");
            capabilities.Add(line.Text.TrimEnd('\n'));
        }
        if (!capabilities.Any(c => c == "fetch" || c.StartsWith("fetch=", StringComparison.Ordinal)) ||
            !capabilities.Any(c => c == "ls-refs" || c.StartsWith("ls-refs=", StringComparison.Ordinal)))
            throw Protocol("the server speaks protocol version 2 without ls-refs and fetch");
        RequireSha1(capabilities.FirstOrDefault(c => c.StartsWith("object-format=", StringComparison.Ordinal))?["object-format=".Length..]);

        using MemoryStream command = new();
        Command(command, "ls-refs", capabilities);
        Packet.Delimiter(command);
        Packet.Write(command, "symrefs\n");
        Packet.Write(command, "peel\n");
        Packet.Write(command, "ref-prefix HEAD\n");
        foreach (string prefix in Prefixes) Packet.Write(command, "ref-prefix " + prefix + "\n");
        Packet.Flush(command);

        using HttpResponseMessage response = await Post(address, command.ToArray(), version2: true, cancellationToken);
        await using Stream body = await Read(response, cancellationToken);
        PacketReader lines = new(body, MaximumAdvertisementBytes);
        Dictionary<string, string> refs = new(StringComparer.Ordinal);
        string? head = null;
        for (Frame line = await lines.NextAsync(cancellationToken); line.Kind != FrameKind.Flush; line = await lines.NextAsync(cancellationToken))
        {
            if (line.Kind != FrameKind.Data) throw Protocol("ls-refs answered with a non-data packet");
            string[] parts = line.Text.TrimEnd('\n').Split(' ');
            if (parts.Length < 2) throw Protocol("ls-refs answered with a malformed line");
            if (parts[1] == "HEAD")
            {
                string? target = parts.Skip(2).FirstOrDefault(p => p.StartsWith("symref-target:", StringComparison.Ordinal))?["symref-target:".Length..];
                if (target is not null && target.StartsWith("refs/heads/", StringComparison.Ordinal)) head = target["refs/heads/".Length..];
                continue;
            }
            Keep(refs, parts[0], parts[1]);
        }
        return new(refs, head, 2, address, capabilities);
    }

    private static async Task<RemoteAdvertisement> ListV0Async(Uri address, PacketReader reader, Frame first, CancellationToken cancellationToken)
    {
        HashSet<string> capabilities = new(StringComparer.Ordinal);
        Dictionary<string, string> refs = new(StringComparer.Ordinal);
        string? head = null;
        bool seenFirst = false;
        for (Frame line = first; line.Kind != FrameKind.Flush; line = await reader.NextAsync(cancellationToken))
        {
            if (line.Kind != FrameKind.Data) throw Protocol("the reference advertisement held a non-data packet");
            string text = line.Text.TrimEnd('\n');
            if (!seenFirst)
            {
                seenFirst = true;
                int nul = text.IndexOf('\0', StringComparison.Ordinal);
                if (nul >= 0)
                {
                    foreach (string capability in text[(nul + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)) capabilities.Add(capability);
                    text = text[..nul];
                }
            }
            string[] parts = text.Split(' ');
            if (parts.Length != 2) throw Protocol("the reference advertisement held a malformed line");
            if (parts[1] is "HEAD" or "capabilities^{}") continue;
            Keep(refs, parts[0], parts[1]);
        }
        RequireSha1(capabilities.FirstOrDefault(c => c.StartsWith("object-format=", StringComparison.Ordinal))?["object-format=".Length..]);
        string? symref = capabilities.FirstOrDefault(c => c.StartsWith("symref=HEAD:refs/heads/", StringComparison.Ordinal));
        if (symref is not null) head = symref["symref=HEAD:refs/heads/".Length..];
        return new(refs, head, 0, address, capabilities);
    }

    /// <summary>
    /// Fetches every object reachable from <paramref name="wants"/> and not from <paramref name="haves"/> as one pack,
    /// written to <paramref name="destination"/> as it arrives. Returns the number of bytes written.
    /// </summary>
    public async Task<long> FetchAsync(RemoteAdvertisement remote, IReadOnlyCollection<string> wants, IReadOnlyCollection<string> haves,
        Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(wants);
        ArgumentNullException.ThrowIfNull(haves);
        if (wants.Count == 0) throw new ArgumentException("Nothing to fetch.", nameof(wants));
        using MemoryStream request = new();
        if (remote.ProtocolVersion == 2)
        {
            Command(request, "fetch", remote.Capabilities);
            Packet.Delimiter(request);
            Packet.Write(request, "ofs-delta\n");
            Packet.Write(request, "no-progress\n");
            foreach (string want in wants) Packet.Write(request, "want " + want + "\n");
            foreach (string have in haves) Packet.Write(request, "have " + have + "\n");
            Packet.Write(request, "done\n");
            Packet.Flush(request);
        }
        else
        {
            // Only what the server offered: a server that did not advertise side-band sends the pack bare.
            string offered = string.Concat(new[] { "ofs-delta", "side-band-64k", "no-progress" }
                .Where(remote.Capabilities.Contains).Select(capability => " " + capability));
            bool first = true;
            foreach (string want in wants)
            {
                Packet.Write(request, "want " + want + (first ? offered + " " + Agent : "") + "\n");
                first = false;
            }
            Packet.Flush(request);
            foreach (string have in haves) Packet.Write(request, "have " + have + "\n");
            Packet.Write(request, "done\n");
        }

        using HttpResponseMessage response = await Post(remote.Address, request.ToArray(), remote.ProtocolVersion == 2, cancellationToken);
        await using Stream body = await Read(response, cancellationToken);
        PackSink sink = new(destination);
        if (remote.ProtocolVersion == 2) await ReceiveV2Async(body, sink, cancellationToken);
        else await ReceiveV0Async(body, sink, remote.Capabilities.Contains("side-band-64k"), cancellationToken);
        if (sink.Written < 32) throw Protocol("the server sent no pack");
        return sink.Written;
    }

    private static async Task ReceiveV2Async(Stream body, PackSink sink, CancellationToken cancellationToken)
    {
        PacketReader reader = new(body, null);
        // Sections before the pack (acknowledgments, shallow-info, wanted-refs, packfile-uris) are read past; with
        // "done" sent and no depth asked for, none of them changes what arrives.
        while (true)
        {
            Frame header = await reader.NextAsync(cancellationToken);
            if (header.Kind != FrameKind.Data) throw Protocol("the fetch response ended before a pack");
            string name = header.Text.TrimEnd('\n');
            if (name.StartsWith("ERR ", StringComparison.Ordinal)) throw Protocol("the server refused the fetch: " + name[4..]);
            if (name == "packfile") break;
            Frame line;
            do line = await reader.NextAsync(cancellationToken);
            while (line.Kind == FrameKind.Data);
            if (line.Kind != FrameKind.Delimiter) throw Protocol("the " + name + " section was not followed by a pack");
        }
        await SidebandAsync(reader, null, sink, cancellationToken);
    }

    private static async Task ReceiveV0Async(Stream body, PackSink sink, bool sideband, CancellationToken cancellationToken)
    {
        PacketReader reader = new(body, null);
        while (true)
        {
            // The pack follows the acknowledgments, either bare, starting "PACK", or in side-band packets.
            if (await reader.PeekPackAsync(cancellationToken))
            {
                await reader.CopyRestAsync(sink, cancellationToken);
                return;
            }
            Frame line = await reader.NextAsync(cancellationToken);
            if (line.Kind != FrameKind.Data) throw Protocol("the fetch response ended before a pack");
            if (line.Text.StartsWith("ACK ", StringComparison.Ordinal) || line.Text.StartsWith("NAK", StringComparison.Ordinal)) continue;
            if (line.Text.StartsWith("ERR ", StringComparison.Ordinal)) throw Protocol("the server refused the fetch: " + line.Text[4..].TrimEnd('\n'));
            if (!sideband) throw Protocol("the fetch response held an unexpected line");
            await SidebandAsync(reader, line, sink, cancellationToken);
            return;
        }
    }

    private static async Task SidebandAsync(PacketReader reader, Frame? first, PackSink sink, CancellationToken cancellationToken)
    {
        for (Frame packet = first ?? await reader.NextAsync(cancellationToken); packet.Kind != FrameKind.Flush; packet = await reader.NextAsync(cancellationToken))
        {
            if (packet.Kind == FrameKind.End) return;
            if (packet.Kind != FrameKind.Data || packet.Bytes.Length == 0) throw Protocol("a side-band packet was malformed");
            switch (packet.Bytes[0])
            {
                case 1: await sink.WriteAsync(packet.Bytes.AsMemory(1), cancellationToken); break;
                case 2: break;
                case 3: throw Protocol("the server reported an error: " + Encoding.UTF8.GetString(packet.Bytes, 1, packet.Bytes.Length - 1).TrimEnd('\n'));
                default: throw Protocol("a side-band packet named an unknown band");
            }
        }
    }

    private static void Command(Stream stream, string command, IReadOnlySet<string> capabilities)
    {
        Packet.Write(stream, "command=" + command + "\n");
        Packet.Write(stream, Agent + "\n");
        if (capabilities.Contains("object-format=sha1")) Packet.Write(stream, "object-format=sha1\n");
    }

    private static void Keep(Dictionary<string, string> refs, string id, string name)
    {
        if (!Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) || name.EndsWith("^{}", StringComparison.Ordinal)) return;
        if (!Limits.IsId(id)) throw Protocol("a reference named an object id that is not SHA-1");
        refs[name] = id;
    }

    private static void RequireSha1(string? format)
    {
        if (format is not null && format != "sha1") throw Protocol("the repository uses " + format + " object ids, and Graft holds SHA-1 repositories only");
    }

    private async Task<HttpResponseMessage> Post(Uri address, byte[] body, bool version2, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, address.ToString().TrimEnd('/') + "/git-upload-pack")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-git-upload-pack-result"));
        if (version2) request.Headers.Add("Git-Protocol", "version=2");
        HttpResponseMessage response = await Send(request, cancellationToken);
        try { Require(response, "application/x-git-upload-pack-result"); }
        catch { response.Dispose(); throw; }
        return response;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("git", "2.0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(Graft)"));
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException error) { throw new GitRemoteException(RemoteFailure.Unreachable, "the server could not be reached: " + Innermost(error), error); }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new GitRemoteException(RemoteFailure.Unreachable, "the server did not answer in time", error); }
    }

    private static string Innermost(Exception error)
    {
        while (error.InnerException is not null) error = error.InnerException;
        return error.Message;
    }

    private static async Task<Stream> Read(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadAsStreamAsync(cancellationToken); }
        catch (HttpRequestException error) { throw new GitRemoteException(RemoteFailure.Unreachable, "the connection failed: " + Innermost(error), error); }
    }

    private static void Require(HttpResponseMessage response, string mediaType)
    {
        int status = (int)response.StatusCode;
        if (status is 401 or 403 or 404 or 410 or 451)
            throw new GitRemoteException(RemoteFailure.Unavailable,
                "the server answered " + status.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase +
                ": the repository is gone, private, or was never there");
        if (status == 429 || status >= 500)
            throw new GitRemoteException(RemoteFailure.Unreachable, "the server answered " + status.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase);
        if (response.StatusCode != HttpStatusCode.OK)
            throw Protocol("the server answered " + status.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase);
        if (response.Content.Headers.ContentType?.MediaType != mediaType)
            throw Protocol("the address is not a Git repository served over smart HTTP");
    }

    private static GitRemoteException Protocol(string message) => new(RemoteFailure.Protocol, message);

    private enum FrameKind { Data, Flush, Delimiter, End }

    private readonly record struct Frame(FrameKind Kind, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }

    /// <summary>Reads pkt-lines from a response, bounded so a hostile server cannot make it read forever into memory.</summary>
    private sealed class PacketReader(Stream source, long? budget)
    {
        private readonly byte[] header = new byte[4];
        private int buffered;
        private long consumed;

        public async Task<Frame> NextAsync(CancellationToken cancellationToken)
        {
            await FillAsync(cancellationToken);
            buffered = 0;
            if (!int.TryParse(Encoding.ASCII.GetString(header), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int length))
                throw Protocol("a packet had a malformed length");
            if (length == 0) return new(FrameKind.Flush, []);
            if (length == 1) return new(FrameKind.Delimiter, []);
            if (length == 2) return new(FrameKind.End, []);
            if (length is < 4 or > 65520) throw Protocol("a packet had an impossible length");
            byte[] payload = new byte[length - 4];
            int offset = 0;
            while (offset < payload.Length)
            {
                int read = await source.ReadAsync(payload.AsMemory(offset), cancellationToken);
                if (read == 0) throw Protocol("the response ended in the middle of a packet");
                offset += read;
                Count(read);
            }
            return new(FrameKind.Data, payload);
        }

        /// <summary>Whether the next bytes begin a bare pack rather than a packet. Leaves them to be read.</summary>
        public async Task<bool> PeekPackAsync(CancellationToken cancellationToken)
        {
            await FillAsync(cancellationToken);
            return header.AsSpan().SequenceEqual("PACK"u8);
        }

        public async Task CopyRestAsync(PackSink sink, CancellationToken cancellationToken)
        {
            await sink.WriteAsync(header.AsMemory(0, buffered), cancellationToken);
            buffered = 0;
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            while (buffered < header.Length)
            {
                int read = await source.ReadAsync(header.AsMemory(buffered), cancellationToken);
                if (read == 0) throw Protocol("the response ended in the middle of a packet");
                buffered += read;
                Count(read);
            }
        }

        private void Count(int read)
        {
            consumed += read;
            if (budget is long limit && consumed > limit) throw new GitRemoteException(RemoteFailure.TooLarge, "the reference list is larger than Graft will read");
        }
    }

    /// <summary>Where pack bytes go, counted against the most a push may send.</summary>
    private sealed class PackSink(Stream destination)
    {
        public long Written { get; private set; }

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Written += data.Length;
            if (Written > Limits.RequestBytes) throw new GitRemoteException(RemoteFailure.TooLarge, "the repository's pack is larger than Graft accepts");
            await destination.WriteAsync(data, cancellationToken);
        }
    }
}
