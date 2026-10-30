using System.Net;
using System.Net.Sockets;

namespace Graft.Ssh;

/// <summary>What the server needs to know to listen. The host key is created by the caller so that its
/// location and its fingerprint's reporting belong to the application.</summary>
public sealed class SshServerOptions
{
    public required IPAddress Address { get; init; }
    public required int Port { get; init; }
    public required SshHostKey HostKey { get; init; }

    /// <summary>Connections served at once; the rest wait in the listen backlog or are refused by the OS.</summary>
    public int MaximumConnections { get; init; } = 64;

    /// <summary>How long a client has to authenticate before the connection is dropped.</summary>
    public TimeSpan AuthenticationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Longest a connection may live however busy it is, which bounds a stalled transfer.</summary>
    public TimeSpan SessionLimit { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Public-key attempts before the connection is dropped. A client with many keys tries each.</summary>
    public int MaximumAuthenticationAttempts { get; init; } = 12;

    /// <summary>Where the server reports what it saw. Never given a secret.</summary>
    public Action<string, Exception?>? Log { get; init; }
}

/// <summary>
/// The application behind the transport. The SSH layer decides nothing about who a key belongs to or what
/// a command means; it hands both questions here.
/// </summary>
public interface ISshHost
{
    /// <summary>The principal this key authenticates, or null when the key is unknown or may not sign in.
    /// Called first with <paramref name="signatureVerified"/> false, so that a client can learn whether to
    /// bother signing, and then again with it true once the signature over the session has verified, which
    /// is the moment that counts as a use of the key.</summary>
    ValueTask<object?> AuthenticateAsync(string username, SshPublicKey key, bool signatureVerified, CancellationToken cancellationToken);

    /// <summary>Runs one command and returns its exit status. Everything the command reads and writes goes
    /// through the streams on <paramref name="command"/>; what it writes to the error stream reaches the
    /// user's terminal.</summary>
    Task<int> ExecuteAsync(SshCommand command, CancellationToken cancellationToken);

    /// <summary>What an interactive login is told before the connection ends. Null for silence.</summary>
    string? Greeting(object principal, string username);
}

/// <summary>One <c>exec</c> request on an authenticated session.</summary>
public sealed class SshCommand
{
    internal SshCommand(object principal, string username, string commandLine, IReadOnlyDictionary<string, string> environment,
        Stream input, Stream output, Func<string, CancellationToken, Task> writeError)
    {
        Principal = principal;
        Username = username;
        CommandLine = commandLine;
        Environment = environment;
        Input = input;
        Output = output;
        WriteErrorCore = writeError;
    }

    public object Principal { get; }
    public string Username { get; }
    public string CommandLine { get; }
    /// <summary>The variables the client asked to set. Only names the server chose to accept appear.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
    /// <summary>The command's standard input. Ends when the client sends EOF.</summary>
    public Stream Input { get; }
    /// <summary>The command's standard output, flow controlled by the client's window.</summary>
    public Stream Output { get; }
    private Func<string, CancellationToken, Task> WriteErrorCore { get; }

    /// <summary>Writes a line to the client's standard error, which Git shows the user as <c>remote:</c> text
    /// or as the failure reason.</summary>
    public Task WriteErrorAsync(string text, CancellationToken cancellationToken) => WriteErrorCore(text, cancellationToken);
}

/// <summary>
/// An SSH server that does one thing: authenticates a client by public key and runs the command it asks
/// for. No passwords, no shells, no forwarding, no subsystems. Written against RFC 4253, 4252 and 4254 with
/// the platform's own cryptography, so it goes wherever the rest of the binary goes.
/// </summary>
public sealed class SshServer(SshServerOptions options, ISshHost host) : IAsyncDisposable
{
    private readonly SemaphoreSlim slots = new(options.MaximumConnections, options.MaximumConnections);
    private readonly CancellationTokenSource stopping = new();
    private TcpListener? listener;
    private Task? accepting;

    /// <summary>The port actually bound, which matters when the caller asked for port zero.</summary>
    public int Port { get; private set; }

    public void Start()
    {
        listener = new(options.Address, options.Port);
        listener.Start(backlog: 128);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync(listener, stopping.Token);
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException exception) { options.Log?.Invoke("accept failed", exception); continue; }
            catch (ObjectDisposedException) { break; }
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ = ServeAsync(socket, cancellationToken);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken cancellationToken)
    {
        string peer = socket.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            socket.NoDelay = true;
            using CancellationTokenSource life = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            life.CancelAfter(options.SessionLimit);
            using NetworkStream stream = new(socket, ownsSocket: true);
            SshConnection connection = new(stream, options, host, peer, life.Token);
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (SshProtocolException exception) { options.Log?.Invoke("ssh " + peer + ": " + exception.Message, null); }
        catch (IOException) { }
        catch (SocketException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { options.Log?.Invoke("ssh " + peer + ": connection failed", exception); }
        finally { slots.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener?.Stop();
        if (accepting is not null)
        {
            try { await accepting.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        stopping.Dispose();
    }
}
