using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Server;

/// <summary>Where mail goes: an SMTP server, how to reach it securely, who to say it is from.</summary>
public sealed record MailSettings(string Host, int Port, MailSecurity Security, string? Username, string? Password, string From)
{
    /// <summary>
    /// Parses <c>smtp://[user@]host[:port]</c>, <c>smtp+starttls://...</c> or <c>smtps://...</c>. Plain
    /// <c>smtp</c> defaults to port 25 and sends in clear, which is for a relay on the same machine or
    /// network and nothing else; <c>smtp+starttls</c> defaults to 587 and insists on upgrading;
    /// <c>smtps</c> defaults to 465 and connects with TLS from the first byte.
    /// </summary>
    public static bool TryParse(string url, string from, string? password, out MailSettings? settings)
    {
        settings = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0 || uri.AbsolutePath is not ("" or "/") ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        MailSecurity security;
        int defaultPort;
        switch (uri.Scheme)
        {
            case "smtp": security = MailSecurity.None; defaultPort = 25; break;
            case "smtp+starttls": security = MailSecurity.StartTls; defaultPort = 587; break;
            case "smtps": security = MailSecurity.Tls; defaultPort = 465; break;
            default: return false;
        }
        string? username = uri.UserInfo.Length == 0 ? null : Uri.UnescapeDataString(uri.UserInfo);
        if (username is not null && username.Contains(':')) return false;
        if ((username is null) != (password is null)) return false;
        if (!IsAddress(from)) return false;
        settings = new(uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port, security, username, password, from);
        return true;
    }

    public static bool IsAddress(string value)
    {
        int at = value.IndexOf('@');
        return value.Length is > 2 and <= 254 && at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1 &&
            !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';' or '"');
    }

    public string Describe() => (Security switch { MailSecurity.None => "smtp", MailSecurity.StartTls => "smtp+starttls", _ => "smtps" }) +
        "://" + Host + ":" + Port.ToString(CultureInfo.InvariantCulture) + " as " + From + (Username is null ? "" : ", authenticated");
}

public enum MailSecurity { None, StartTls, Tls }

/// <summary>
/// Sends the few messages the forge has to send, out of the way of the request that caused them.
///
/// <para>
/// A message is queued and the request returns; a worker delivers. The queue is bounded, in memory, and lost
/// on restart, and a message that fails three times is logged and dropped. That is enough for a password
/// reset link, which a person asks for again if it does not come, and it is all the forge sends. Nothing
/// here is a mail server: there is no store, no bounce handling, and no promise.
/// </para>
///
/// <para>
/// The SMTP client is written here, in about a hundred lines, rather than taken from a package: the forge
/// needs EHLO, STARTTLS, AUTH PLAIN or LOGIN, and one message, and the in-box client has not been
/// recommended for years. Every message is plain text, UTF-8, Base64-encoded in the body so no server's
/// line-length or 8-bit rules can touch it.
/// </para>
/// </summary>
public sealed class MailSender : IAsyncDisposable
{
    private const int QueueDepth = 256;
    private static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    private readonly MailSettings settings;
    private readonly Action<string, Exception?> log;
    private readonly ConcurrentQueue<Message> queue = new();
    private readonly SemaphoreSlim pending = new(0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private int depth;

    public MailSender(MailSettings settings, Action<string, Exception?> log)
    {
        this.settings = settings;
        this.log = log;
        worker = Task.Run(RunAsync);
    }

    private sealed record Message(string To, string Subject, string Body);

    /// <summary>Queues a message. Returns at once; a full queue drops the oldest and says so in the log.</summary>
    public void Send(string to, string subject, string body)
    {
        queue.Enqueue(new(to, subject, body));
        if (Interlocked.Increment(ref depth) > QueueDepth && queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref depth);
            log("mail queue full; the oldest message was dropped", null);
            return;
        }
        pending.Release();
    }

    private async Task RunAsync()
    {
        CancellationToken token = stopping.Token;
        try
        {
            while (true)
            {
                await pending.WaitAsync(token).ConfigureAwait(false);
                if (!queue.TryDequeue(out Message? message)) continue;
                Interlocked.Decrement(ref depth);
                for (int attempt = 0; attempt < Backoff.Length; attempt++)
                {
                    if (Backoff[attempt] > TimeSpan.Zero) await Task.Delay(Backoff[attempt], token).ConfigureAwait(false);
                    try
                    {
                        await DeliverAsync(message, token).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception exception) when (exception is IOException or SocketException or SmtpException or AuthenticationException)
                    {
                        if (attempt == Backoff.Length - 1) log("mail to " + message.To + " was not delivered: " + exception.Message, null);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One SMTP conversation per message. Short, and it does not hold a connection open between them.</summary>
    private async Task DeliverAsync(Message message, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken ct = timeout.Token;
        using TcpClient tcp = new();
        await tcp.ConnectAsync(settings.Host, settings.Port, ct).ConfigureAwait(false);
        Stream stream = tcp.GetStream();
        if (settings.Security == MailSecurity.Tls) stream = await SecureAsync(stream, ct).ConfigureAwait(false);
        SmtpConversation smtp = new(stream);
        await smtp.ExpectAsync(220, ct).ConfigureAwait(false);
        string[] extensions = await smtp.CommandAsync("EHLO graft", 250, ct).ConfigureAwait(false);
        if (settings.Security == MailSecurity.StartTls)
        {
            if (!extensions.Any(line => line.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                throw new SmtpException("the server does not offer STARTTLS");
            await smtp.CommandAsync("STARTTLS", 220, ct).ConfigureAwait(false);
            stream = await SecureAsync(stream, ct).ConfigureAwait(false);
            smtp = new(stream);
            extensions = await smtp.CommandAsync("EHLO graft", 250, ct).ConfigureAwait(false);
        }
        if (settings.Username is not null)
        {
            string? auth = extensions.FirstOrDefault(line => line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase));
            string[] mechanisms = auth is null ? [] : auth[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (mechanisms.Contains("PLAIN", StringComparer.OrdinalIgnoreCase))
            {
                string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + settings.Username + "\0" + settings.Password));
                await smtp.CommandAsync("AUTH PLAIN " + credentials, 235, ct).ConfigureAwait(false);
            }
            else if (mechanisms.Contains("LOGIN", StringComparer.OrdinalIgnoreCase))
            {
                await smtp.CommandAsync("AUTH LOGIN", 334, ct).ConfigureAwait(false);
                await smtp.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username)), 334, ct).ConfigureAwait(false);
                await smtp.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Password!)), 235, ct).ConfigureAwait(false);
            }
            else throw new SmtpException("the server offers no authentication mechanism this client speaks (PLAIN or LOGIN)");
        }
        await smtp.CommandAsync("MAIL FROM:<" + settings.From + ">", 250, ct).ConfigureAwait(false);
        await smtp.CommandAsync("RCPT TO:<" + message.To + ">", 250, ct).ConfigureAwait(false);
        await smtp.CommandAsync("DATA", 354, ct).ConfigureAwait(false);
        await smtp.DataAsync(Compose(message), ct).ConfigureAwait(false);
        await smtp.CommandAsync("QUIT", 221, ct).ConfigureAwait(false);
    }

    private async Task<Stream> SecureAsync(Stream stream, CancellationToken ct)
    {
        SslStream ssl = new(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = settings.Host }, ct).ConfigureAwait(false);
        return ssl;
    }

    /// <summary>RFC 5322 headers and a Base64 body. The subject is encoded when it is not plain ASCII.</summary>
    private string Compose(Message message)
    {
        StringBuilder text = new();
        text.Append("From: Graft <").Append(settings.From).Append(">\r\n");
        text.Append("To: <").Append(message.To).Append(">\r\n");
        text.Append("Subject: ").Append(EncodeHeader(message.Subject)).Append("\r\n");
        text.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        text.Append("Message-ID: <").Append(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))).Append('@')
            .Append(settings.From[(settings.From.IndexOf('@') + 1)..]).Append(">\r\n");
        text.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\nAuto-Submitted: auto-generated\r\n\r\n");
        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Body));
        for (int offset = 0; offset < body.Length; offset += 76)
            text.Append(body, offset, Math.Min(76, body.Length - offset)).Append("\r\n");
        return text.ToString();
    }

    private static string EncodeHeader(string value) =>
        value.All(c => c is >= ' ' and <= '~') ? value : "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        stopping.Dispose();
        pending.Dispose();
    }

    /// <summary>The line protocol: a command, then one or more reply lines sharing a three-digit code.</summary>
    private sealed class SmtpConversation(Stream stream)
    {
        private readonly StreamReader reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

        public async Task<string[]> CommandAsync(string command, int expected, CancellationToken ct)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), ct).ConfigureAwait(false);
            return await ExpectAsync(expected, ct).ConfigureAwait(false);
        }

        public async Task DataAsync(string message, CancellationToken ct)
        {
            // A line beginning with a period is doubled, and the message ends with a lone period.
            string dotStuffed = message.Replace("\r\n.", "\r\n..", StringComparison.Ordinal);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(dotStuffed + "\r\n.\r\n"), ct).ConfigureAwait(false);
            await ExpectAsync(250, ct).ConfigureAwait(false);
        }

        /// <summary>Reads a reply and returns its lines' text; throws when the code is not the expected one.</summary>
        public async Task<string[]> ExpectAsync(int expected, CancellationToken ct)
        {
            List<string> lines = [];
            while (true)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new SmtpException("the server closed the connection");
                if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                    throw new SmtpException("malformed reply: " + line);
                lines.Add(line.Length > 4 ? line[4..] : "");
                if (line.Length == 3 || line[3] == ' ')
                {
                    if (code != expected) throw new SmtpException("expected " + expected.ToString(CultureInfo.InvariantCulture) + ", got: " + line);
                    return [.. lines];
                }
                if (lines.Count > 64) throw new SmtpException("reply too long");
            }
        }
    }
}

public sealed class SmtpException(string message) : Exception(message);
