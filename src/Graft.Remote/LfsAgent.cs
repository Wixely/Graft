using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Graft.Remote;

/// <summary>
/// A Git LFS custom transfer agent named <c>graft</c> (ADR 0043). git-lfs still asks the forge's batch API what to send;
/// when the forge answers with the <c>graft</c> transfer, git-lfs starts this program and hands it each object with the
/// address and credential the forge gave. An upload goes as a chunked transfer, in parts no bigger than the forge
/// allows; a download is read in ranges and resumed where a dropped connection left it, then checked against its oid.
///
/// <para>
/// The protocol is git-lfs's: one JSON message a line on standard input and output, <c>init</c> first, then
/// <c>upload</c> or <c>download</c> events each answered with progress and a <c>complete</c>, and <c>terminate</c> to end.
/// </para>
/// </summary>
internal static class LfsAgent
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using StreamReader input = new(Console.OpenStandardInput());
        await using StreamWriter output = new(Console.OpenStandardOutput()) { AutoFlush = true, NewLine = "\n" };
        string operation = "";
        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken)) is not null)
        {
            JsonNode? message = JsonNode.Parse(line);
            string kind = message?["event"]?.GetValue<string>() ?? "";
            if (kind == "init")
            {
                operation = message!["operation"]?.GetValue<string>() ?? "";
                await output.WriteLineAsync("{}");
            }
            else if (kind == "terminate") return 0;
            else if (kind is "upload" or "download")
            {
                string oid = message!["oid"]?.GetValue<string>() ?? "";
                JsonObject reply = new() { ["event"] = "complete", ["oid"] = oid };
                try
                {
                    string? path = kind == "upload" ? await UploadAsync(message, output, cancellationToken) : await DownloadAsync(message, output, cancellationToken);
                    if (path is not null) reply["path"] = path;
                }
                catch (Exception error) when (error is HelperException or HttpRequestException or IOException)
                {
                    reply["error"] = new JsonObject { ["code"] = 1, ["message"] = error.Message };
                }
                await output.WriteLineAsync(reply.ToJsonString());
            }
            else
            {
                await output.WriteLineAsync(new JsonObject { ["error"] = new JsonObject { ["code"] = 32, ["message"] = "unknown event " + kind + " during " + operation } }.ToJsonString());
            }
        }
        return 0;
    }

    private static async Task<string?> UploadAsync(JsonNode message, StreamWriter output, CancellationToken cancellationToken)
    {
        string oid = message["oid"]!.GetValue<string>();
        string path = message["path"]?.GetValue<string>() ?? throw new HelperException("git-lfs named no file to upload");
        (Uri href, Dictionary<string, string> headers) = Action(message);
        using ForgeClient forge = new(href, headers, text => Console.Error.WriteLine(text));
        await using FileStream content = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        using ProgressStream counted = new(content, (total, delta) => Progress(output, oid, total, delta));
        await forge.TransferAsync(href, counted, null, "file " + oid[..12], cancellationToken);
        return null;
    }

    private static async Task<string> DownloadAsync(JsonNode message, StreamWriter output, CancellationToken cancellationToken)
    {
        string oid = message["oid"]!.GetValue<string>();
        long size = message["size"]?.GetValue<long>() ?? 0;
        (Uri href, Dictionary<string, string> headers) = Action(message);
        using ForgeClient forge = new(href, headers, text => Console.Error.WriteLine(text));
        string directory = Path.Combine(Environment.GetEnvironmentVariable("GIT_DIR") is { Length: > 0 } gitDir ? gitDir : ".git", "lfs", "tmp");
        if (!Directory.Exists(directory)) directory = Path.GetTempPath();
        string path = Path.Combine(directory, "graft-" + oid + "-" + Guid.NewGuid().ToString("N"));
        await using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            using ProgressStream counted = new(file, (total, delta) => Progress(output, oid, total, delta));
            await forge.ReadResultAsync(href, size, counted, "file " + oid[..12], cancellationToken);
        }
        string actual;
        await using (FileStream check = File.OpenRead(path))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancellationToken));
        if (actual != oid)
        {
            File.Delete(path);
            throw new HelperException("the downloaded file does not hash to " + oid);
        }
        return path;
    }

    private static (Uri Href, Dictionary<string, string> Headers) Action(JsonNode message)
    {
        JsonNode action = message["action"] ?? throw new HelperException("git-lfs gave no action for " + message["oid"]);
        Uri href = new(action["href"]?.GetValue<string>() ?? throw new HelperException("the action has no address"));
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        if (action["header"] is JsonObject header)
            foreach ((string name, JsonNode? value) in header)
                if (value is not null) headers[name] = value.GetValue<string>();
        return (href, headers);
    }

    private static void Progress(StreamWriter output, string oid, long total, long delta) =>
        output.WriteLine(new JsonObject { ["event"] = "progress", ["oid"] = oid, ["bytesSoFar"] = total, ["bytesSinceLast"] = delta }.ToJsonString());

    /// <summary>Counts bytes through a stream for git-lfs's progress; reported at most every four MiB.</summary>
    private sealed class ProgressStream(Stream inner, Action<long, long> report) : Stream
    {
        private long total;
        private long reported;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            Count(inner.Position);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            Count(inner.Position);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Count(total + count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Count(total + buffer.Length);
        }

        /// <summary>Progress is how far through the file, which for an upload is the furthest position read.</summary>
        private void Count(long position)
        {
            if (position <= total) return;
            total = position;
            if (total - reported >= 4 * 1024 * 1024 || (CanSeek && total == inner.Length))
            {
                report(total, total - reported);
                reported = total;
            }
        }
    }
}
