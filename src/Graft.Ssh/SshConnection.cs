using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Ssh;

/// <summary>
/// One client connection, from the version line to the disconnect: the key exchange of RFC 4253, the
/// public-key authentication of RFC 4252 and the single session channel of RFC 4254 that carries a command.
///
/// <para>
/// One task reads packets in order and acts on each; the command runs on another, and everything that goes
/// to the wire passes through one lock, so a key exchange can hold it while the command's output waits. The
/// algorithm set is small on purpose: ECDH over P-256 for key agreement, an ECDSA P-256 host key, AES in
/// GCM with AES-CTR and HMAC-SHA-256 as the fallback, and public keys of the three families clients
/// actually have. Every one of those is in the platform except Ed25519 verification, which is in this
/// assembly.
/// </para>
/// </summary>
internal sealed class SshConnection(Stream stream, SshServerOptions options, ISshHost host, string peer, CancellationToken lifetime)
{
    private const string ServerVersion = "SSH-2.0-Graft";
    private const string KexAlgorithm = "ecdh-sha2-nistp256";
    private static readonly string[] Ciphers = ["aes256-gcm@openssh.com", "aes128-gcm@openssh.com", "aes256-ctr", "aes128-ctr"];
    private const string Mac = "hmac-sha2-256";
    private const uint LocalWindowSize = 2 * 1024 * 1024;
    private const uint LocalMaximumPacket = 32 * 1024;
    private const uint WindowReplenishAt = LocalWindowSize / 2;

    // Message numbers, RFC 4250 section 4.1.3.
    private const byte Disconnect = 1, Ignore = 2, Unimplemented = 3, Debug = 4, ServiceRequest = 5, ServiceAccept = 6, ExtInfo = 7;
    private const byte KexInit = 20, NewKeys = 21, KexEcdhInit = 30, KexEcdhReply = 31;
    private const byte UserauthRequest = 50, UserauthFailure = 51, UserauthSuccess = 52, UserauthPkOk = 60;
    private const byte GlobalRequest = 80, RequestFailure = 82;
    private const byte ChannelOpen = 90, ChannelOpenConfirmation = 91, ChannelOpenFailure = 92, ChannelWindowAdjust = 93;
    private const byte ChannelData = 94, ChannelExtendedData = 95, ChannelEof = 96, ChannelClose = 97, ChannelRequest = 98;
    private const byte ChannelSuccess = 99, ChannelFailure = 100;

    private readonly SshServerOptions options = options;
    private readonly string peer = peer;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource closing = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private PacketCipher receive = new PlainCipher();
    private PacketCipher send = new PlainCipher();
    private uint receiveSequence;
    private uint sendSequence;
    private byte[]? sessionId;
    private string clientVersion = "";
    private byte[]? pendingKexInit;
    private bool clientWantsExtInfo;

    private object? principal;
    private string username = "";
    private int authenticationAttempts;

    private Channel? channel;

    public async Task RunAsync()
    {
        CancellationToken token = closing.Token;
        await ExchangeVersionsAsync(token).ConfigureAwait(false);

        // The server's KEXINIT goes first; the client's follows and the exchange runs from there.
        await writeLock.WaitAsync(token).ConfigureAwait(false);
        try { pendingKexInit = await WriteKexInitAsync(token).ConfigureAwait(false); }
        finally { writeLock.Release(); }

        using CancellationTokenSource authentication = CancellationTokenSource.CreateLinkedTokenSource(token);
        authentication.CancelAfter(options.AuthenticationTimeout);

        while (true)
        {
            CancellationToken readToken = principal is null ? authentication.Token : token;
            byte[] packet = await ReadPacketAsync(readToken).ConfigureAwait(false);
            switch (packet[0])
            {
                case Disconnect:
                    await EndChannelAsync().ConfigureAwait(false);
                    return;
                case Ignore or Debug or Unimplemented:
                    break;
                case KexInit:
                    await KeyExchangeAsync(packet, token).ConfigureAwait(false);
                    break;
                case ServiceRequest:
                    await OnServiceRequestAsync(packet, token).ConfigureAwait(false);
                    break;
                case UserauthRequest:
                    await OnUserauthRequestAsync(packet, token).ConfigureAwait(false);
                    break;
                case GlobalRequest:
                    await OnGlobalRequestAsync(packet, token).ConfigureAwait(false);
                    break;
                case ChannelOpen:
                    RequireAuthenticated();
                    await OnChannelOpenAsync(packet, token).ConfigureAwait(false);
                    break;
                case ChannelRequest:
                    await OnChannelRequestAsync(packet, token).ConfigureAwait(false);
                    break;
                case ChannelData:
                    await OnChannelDataAsync(packet, token).ConfigureAwait(false);
                    break;
                case ChannelExtendedData:
                    // The client's standard error has nowhere to go; it is consumed against the window.
                    await OnChannelDataAsync(packet, token, discard: true).ConfigureAwait(false);
                    break;
                case ChannelWindowAdjust:
                    OnWindowAdjust(packet);
                    break;
                case ChannelEof:
                    await OnChannelEofAsync(packet).ConfigureAwait(false);
                    break;
                case ChannelClose:
                    if (await OnChannelCloseAsync(packet, token).ConfigureAwait(false)) return;
                    break;
                default:
                    await SendAsync(new SshWriter().Byte(Unimplemented).UInt32(receiveSequence - 1).ToArray(), token).ConfigureAwait(false);
                    break;
            }
        }
    }

    // -----------------------------------------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------------------------------------

    private async Task ExchangeVersionsAsync(CancellationToken token)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(ServerVersion + "\r\n"), token).ConfigureAwait(false);
        // The client may send lines before its version line; each is bounded and so is their number.
        for (int lines = 0; lines < 20; lines++)
        {
            string line = await ReadLineAsync(token).ConfigureAwait(false);
            if (!line.StartsWith("SSH-", StringComparison.Ordinal)) continue;
            if (!line.StartsWith("SSH-2.0-", StringComparison.Ordinal)) throw new SshProtocolException("unsupported protocol version");
            clientVersion = line;
            return;
        }
        throw new SshProtocolException("no version line");
    }

    private async Task<string> ReadLineAsync(CancellationToken token)
    {
        byte[] line = new byte[255];
        int length = 0;
        byte[] one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one, token).ConfigureAwait(false);
            if (read == 0) throw new SshProtocolException("connection closed before version line");
            if (one[0] == (byte)'\n') break;
            if (length == line.Length) throw new SshProtocolException("version line too long");
            line[length++] = one[0];
        }
        if (length > 0 && line[length - 1] == (byte)'\r') length--;
        return Encoding.ASCII.GetString(line, 0, length);
    }

    private async Task<byte[]> ReadPacketAsync(CancellationToken token)
    {
        byte[] payload = await receive.OpenAsync(stream, receiveSequence, token).ConfigureAwait(false);
        receiveSequence++;
        if (payload.Length == 0) throw new SshProtocolException("empty payload");
        return payload;
    }

    private async Task SendAsync(byte[] payload, CancellationToken token)
    {
        await writeLock.WaitAsync(token).ConfigureAwait(false);
        try { await WriteUnlockedAsync(payload, token).ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    private async Task WriteUnlockedAsync(byte[] payload, CancellationToken token)
    {
        byte[] packet = send.Seal(payload, sendSequence);
        sendSequence++;
        await stream.WriteAsync(packet, token).ConfigureAwait(false);
    }

    private async Task DisconnectAsync(uint reason, string description, CancellationToken token)
    {
        try
        {
            await SendAsync(new SshWriter().Byte(Disconnect).UInt32(reason).String(description).String("").ToArray(), token).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    // -----------------------------------------------------------------------------------------------
    // Key exchange
    // -----------------------------------------------------------------------------------------------

    private async Task<byte[]> WriteKexInitAsync(CancellationToken token)
    {
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        byte[] payload = new SshWriter().Byte(KexInit).Bytes(cookie)
            .NameList([KexAlgorithm, "ext-info-s"])
            .NameList([SshHostKey.Algorithm])
            .NameList(Ciphers).NameList(Ciphers)
            .NameList([Mac]).NameList([Mac])
            .NameList(["none"]).NameList(["none"])
            .NameList([]).NameList([])
            .Boolean(false).UInt32(0).ToArray();
        await WriteUnlockedAsync(payload, token).ConfigureAwait(false);
        return payload;
    }

    private async Task KeyExchangeAsync(byte[] clientKexInit, CancellationToken token)
    {
        await writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] serverKexInit = pendingKexInit ?? await WriteKexInitAsync(token).ConfigureAwait(false);
            pendingKexInit = null;

            SshReader reader = new(clientKexInit.AsMemory(1));
            reader.Bytes(16);
            string[] kexAlgorithms = reader.NameList();
            string[] hostKeyAlgorithms = reader.NameList();
            string[] ciphersToServer = reader.NameList();
            string[] ciphersToClient = reader.NameList();
            string[] macsToServer = reader.NameList();
            string[] macsToClient = reader.NameList();
            string[] compressionToServer = reader.NameList();
            string[] compressionToClient = reader.NameList();
            reader.NameList();
            reader.NameList();
            bool guessFollows = reader.Boolean();
            reader.UInt32();

            if (sessionId is null) clientWantsExtInfo = kexAlgorithms.Contains("ext-info-c", StringComparer.Ordinal);
            if (!kexAlgorithms.Contains(KexAlgorithm, StringComparer.Ordinal)) throw new SshProtocolException("no common key exchange algorithm");
            if (!hostKeyAlgorithms.Contains(SshHostKey.Algorithm, StringComparer.Ordinal)) throw new SshProtocolException("no common host key algorithm");
            string cipherToServer = ciphersToServer.FirstOrDefault(name => Ciphers.Contains(name, StringComparer.Ordinal))
                ?? throw new SshProtocolException("no common cipher");
            string cipherToClient = ciphersToClient.FirstOrDefault(name => Ciphers.Contains(name, StringComparer.Ordinal))
                ?? throw new SshProtocolException("no common cipher");
            if (!macsToServer.Contains(Mac, StringComparer.Ordinal) || !macsToClient.Contains(Mac, StringComparer.Ordinal))
                throw new SshProtocolException("no common MAC");
            if (!compressionToServer.Contains("none", StringComparer.Ordinal) || !compressionToClient.Contains("none", StringComparer.Ordinal))
                throw new SshProtocolException("no common compression");
            // A client that guessed wrong sends a key exchange packet for an algorithm that was not chosen.
            bool discardGuess = guessFollows && (kexAlgorithms[0] != KexAlgorithm || hostKeyAlgorithms[0] != SshHostKey.Algorithm);

            byte[] init = await ReadKexPacketAsync(KexEcdhInit, discardGuess, token).ConfigureAwait(false);
            SshReader initReader = new(init.AsMemory(1));
            byte[] clientPoint = initReader.String().ToArray();
            initReader.ExpectEnd();
            if (clientPoint.Length != 65 || clientPoint[0] != 4) throw new SshProtocolException("bad client ECDH point");

            using ECDiffieHellman ours = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ECParameters ourParameters = ours.ExportParameters(false);
            byte[] serverPoint = [4, .. ourParameters.Q.X!, .. ourParameters.Q.Y!];
            byte[] shared;
            using (ECDiffieHellman theirs = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = clientPoint[1..33], Y = clientPoint[33..65] }
            }))
            {
                try { shared = ours.DeriveRawSecretAgreement(theirs.PublicKey); }
                catch (CryptographicException) { throw new SshProtocolException("bad client ECDH point"); }
            }

            byte[] sharedMpint = new SshWriter().MPInt(shared).ToArray();
            byte[] exchangeHash = SHA256.HashData(new SshWriter()
                .String(clientVersion).String(ServerVersion)
                .String(clientKexInit).String(serverKexInit)
                .String(options.HostKey.Blob)
                .String(clientPoint).String(serverPoint)
                .Bytes(sharedMpint).ToArray());
            bool first = sessionId is null;
            sessionId ??= exchangeHash;

            await WriteUnlockedAsync(new SshWriter().Byte(KexEcdhReply)
                .String(options.HostKey.Blob).String(serverPoint).String(options.HostKey.Sign(exchangeHash)).ToArray(), token).ConfigureAwait(false);
            await WriteUnlockedAsync([NewKeys], token).ConfigureAwait(false);

            PacketCipher newSend = CipherFor(cipherToClient, sharedMpint, exchangeHash, 'B', 'D', 'F');
            PacketCipher newReceive = CipherFor(cipherToServer, sharedMpint, exchangeHash, 'A', 'C', 'E');
            (send as IDisposable)?.Dispose();
            send = newSend;

            await ReadKexPacketAsync(NewKeys, false, token).ConfigureAwait(false);
            (receive as IDisposable)?.Dispose();
            receive = newReceive;

            if (first && clientWantsExtInfo)
                await WriteUnlockedAsync(new SshWriter().Byte(ExtInfo).UInt32(1)
                    .String("server-sig-algs").NameList(SshPublicKey.SignatureAlgorithms).ToArray(), token).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    /// <summary>Reads the next key exchange packet of the expected kind, skipping what may be interleaved.</summary>
    private async Task<byte[]> ReadKexPacketAsync(byte expected, bool discardFirst, CancellationToken token)
    {
        bool discard = discardFirst;
        while (true)
        {
            byte[] packet = await ReadPacketAsync(token).ConfigureAwait(false);
            if (packet[0] is Ignore or Debug) continue;
            if (packet[0] == Disconnect) throw new SshProtocolException("client disconnected during key exchange");
            if (discard) { discard = false; continue; }
            if (packet[0] != expected) throw new SshProtocolException("unexpected message during key exchange");
            return packet;
        }
    }

    private PacketCipher CipherFor(string name, byte[] sharedMpint, byte[] exchangeHash, char ivLetter, char keyLetter, char macLetter)
    {
        int keyBytes = name.StartsWith("aes256", StringComparison.Ordinal) ? 32 : 16;
        bool gcm = name.EndsWith("-gcm@openssh.com", StringComparison.Ordinal);
        byte[] key = Derive(sharedMpint, exchangeHash, keyLetter, keyBytes);
        byte[] iv = Derive(sharedMpint, exchangeHash, ivLetter, gcm ? 12 : 16);
        return gcm ? new GcmCipher(key, iv) : new CtrCipher(key, iv, Derive(sharedMpint, exchangeHash, macLetter, 32));
    }

    /// <summary>RFC 4253 section 7.2: HASH(K || H || letter || session_id), extended by hashing K || H || previous.</summary>
    private byte[] Derive(byte[] sharedMpint, byte[] exchangeHash, char letter, int length)
    {
        byte[] result = SHA256.HashData([.. sharedMpint, .. exchangeHash, (byte)letter, .. sessionId!]);
        while (result.Length < length)
            result = [.. result, .. SHA256.HashData([.. sharedMpint, .. exchangeHash, .. result])];
        return result[..length];
    }

    // -----------------------------------------------------------------------------------------------
    // Authentication
    // -----------------------------------------------------------------------------------------------

    private async Task OnServiceRequestAsync(byte[] packet, CancellationToken token)
    {
        SshReader reader = new(packet.AsMemory(1));
        string service = reader.Text();
        if (service != "ssh-userauth") throw new SshProtocolException("unsupported service " + service);
        await SendAsync(new SshWriter().Byte(ServiceAccept).String(service).ToArray(), token).ConfigureAwait(false);
    }

    private async Task OnUserauthRequestAsync(byte[] packet, CancellationToken token)
    {
        if (principal is not null) return;
        SshReader reader = new(packet.AsMemory(1));
        string user = reader.Text();
        string service = reader.Text();
        string method = reader.Text();
        if (service != "ssh-connection") throw new SshProtocolException("unsupported service " + service);
        if (user.Length is 0 or > 100) throw new SshProtocolException("bad user name");

        if (method != "publickey")
        {
            await FailAuthenticationAsync(token).ConfigureAwait(false);
            return;
        }
        if (++authenticationAttempts > options.MaximumAuthenticationAttempts)
        {
            await DisconnectAsync(14, "too many authentication attempts", token).ConfigureAwait(false);
            throw new SshProtocolException("too many authentication attempts");
        }

        bool signed = reader.Boolean();
        string algorithm = reader.Text();
        byte[] blob = reader.String().ToArray();
        string keyType;
        try { keyType = new SshReader(blob).Text(); }
        catch (SshProtocolException) { await FailAuthenticationAsync(token).ConfigureAwait(false); return; }
        SshPublicKey? key = SshPublicKey.FromBlob(keyType, blob);
        bool algorithmFits = key is not null && SshPublicKey.SignatureAlgorithms.Contains(algorithm, StringComparer.Ordinal) &&
            (keyType == "ssh-rsa" ? algorithm.StartsWith("rsa-sha2-", StringComparison.Ordinal) : algorithm == keyType);
        if (key is null || !algorithmFits)
        {
            await FailAuthenticationAsync(token).ConfigureAwait(false);
            return;
        }

        if (!signed)
        {
            // The query form: the client asks whether this key would be accepted before it signs anything.
            object? candidate = await host.AuthenticateAsync(user, key, false, token).ConfigureAwait(false);
            if (candidate is null)
            {
                options.Log?.Invoke("ssh " + peer + ": key " + key.Fingerprint + " refused", null);
                await FailAuthenticationAsync(token).ConfigureAwait(false);
                return;
            }
            await SendAsync(new SshWriter().Byte(UserauthPkOk).String(algorithm).String(blob).ToArray(), token).ConfigureAwait(false);
            return;
        }

        // The signature is checked before the key is looked up, so a lookup that records a use is only ever
        // made for a client that has proved it holds the private key.
        ReadOnlyMemory<byte> signature = reader.String();
        reader.ExpectEnd();
        byte[] signedData = new SshWriter().String(sessionId!).Byte(UserauthRequest).String(user).String(service)
            .String(method).Boolean(true).String(algorithm).String(blob).ToArray();
        if (!key.Verify(signedData, signature))
        {
            options.Log?.Invoke("ssh " + peer + ": signature by " + key.Fingerprint + " did not verify", null);
            await FailAuthenticationAsync(token).ConfigureAwait(false);
            return;
        }
        object? found = await host.AuthenticateAsync(user, key, true, token).ConfigureAwait(false);
        if (found is null)
        {
            options.Log?.Invoke("ssh " + peer + ": key " + key.Fingerprint + " refused", null);
            await FailAuthenticationAsync(token).ConfigureAwait(false);
            return;
        }

        principal = found;
        username = user;
        options.Log?.Invoke("ssh " + peer + ": authenticated " + key.Fingerprint, null);
        await SendAsync([UserauthSuccess], token).ConfigureAwait(false);
    }

    private Task FailAuthenticationAsync(CancellationToken token) =>
        SendAsync(new SshWriter().Byte(UserauthFailure).NameList(["publickey"]).Boolean(false).ToArray(), token);

    private void RequireAuthenticated()
    {
        if (principal is null) throw new SshProtocolException("connection protocol message before authentication");
    }

    // -----------------------------------------------------------------------------------------------
    // Connection protocol: one session channel, one command
    // -----------------------------------------------------------------------------------------------

    private async Task OnGlobalRequestAsync(byte[] packet, CancellationToken token)
    {
        RequireAuthenticated();
        SshReader reader = new(packet.AsMemory(1));
        reader.Text();
        if (reader.Boolean()) await SendAsync([RequestFailure], token).ConfigureAwait(false);
    }

    private async Task OnChannelOpenAsync(byte[] packet, CancellationToken token)
    {
        SshReader reader = new(packet.AsMemory(1));
        string type = reader.Text();
        uint remoteId = reader.UInt32();
        uint remoteWindow = reader.UInt32();
        uint remoteMaximumPacket = reader.UInt32();
        if (type != "session" || channel is not null)
        {
            await SendAsync(new SshWriter().Byte(ChannelOpenFailure).UInt32(remoteId).UInt32(type == "session" ? 1u : 3u)
                .String(type == "session" ? "one session per connection" : "only sessions are served").String("").ToArray(), token).ConfigureAwait(false);
            return;
        }
        if (remoteMaximumPacket < 1024) throw new SshProtocolException("client maximum packet too small");
        channel = new(this, remoteId, remoteWindow, (int)Math.Min(remoteMaximumPacket, 64 * 1024));
        await SendAsync(new SshWriter().Byte(ChannelOpenConfirmation).UInt32(remoteId).UInt32(0)
            .UInt32(LocalWindowSize).UInt32(LocalMaximumPacket).ToArray(), token).ConfigureAwait(false);
    }

    private Channel RequireChannel(SshReader reader)
    {
        RequireAuthenticated();
        if (reader.UInt32() != 0 || channel is null) throw new SshProtocolException("unknown channel");
        return channel;
    }

    private async Task OnChannelRequestAsync(byte[] packet, CancellationToken token)
    {
        SshReader reader = new(packet.AsMemory(1));
        Channel session = RequireChannel(reader);
        string type = reader.Text();
        bool wantReply = reader.Boolean();
        bool ok = false;
        switch (type)
        {
            case "env":
            {
                string name = reader.Text();
                string value = reader.Text();
                // Only what Git needs to say, and only in a shape it would say it in.
                if (name == "GIT_PROTOCOL" && value.Length <= 64 && value.All(c => c is >= ' ' and <= '~'))
                {
                    session.Environment[name] = value;
                    ok = true;
                }
                break;
            }
            case "exec":
            {
                string command = reader.Text();
                if (command.Length is > 0 and <= 4096 && !session.Started)
                {
                    ok = true;
                    session.Start(() => host.ExecuteAsync(session.CommandFor(principal!, username, command), session.Token));
                }
                break;
            }
            case "shell":
                if (!session.Started)
                {
                    ok = true;
                    string? greeting = host.Greeting(principal!, username);
                    session.Start(async () =>
                    {
                        if (greeting is not null)
                            await session.Output.WriteAsync(Encoding.UTF8.GetBytes(greeting + "\r\n"), session.Token).ConfigureAwait(false);
                        return 0;
                    });
                }
                break;
            default:
                // pty-req, signal, window-change, subsystem, x11-req and the rest: not provided.
                break;
        }
        if (wantReply)
            await SendAsync(new SshWriter().Byte(ok ? ChannelSuccess : ChannelFailure).UInt32(session.RemoteId).ToArray(), token).ConfigureAwait(false);
    }

    private async Task OnChannelDataAsync(byte[] packet, CancellationToken token, bool discard = false)
    {
        SshReader reader = new(packet.AsMemory(1));
        Channel session = RequireChannel(reader);
        if (discard) reader.UInt32();
        ReadOnlyMemory<byte> data = reader.String();
        reader.ExpectEnd();
        if (data.Length > session.LocalWindow) throw new SshProtocolException("client exceeded the window");
        session.LocalWindow -= (uint)data.Length;
        if (discard || session.InputClosed)
        {
            await session.ConsumedAsync(data.Length, token).ConfigureAwait(false);
            return;
        }
        await session.Input.Writer.WriteAsync(data, token).ConfigureAwait(false);
    }

    private void OnWindowAdjust(byte[] packet)
    {
        SshReader reader = new(packet.AsMemory(1));
        Channel session = RequireChannel(reader);
        session.Grant(reader.UInt32());
    }

    private async Task OnChannelEofAsync(byte[] packet)
    {
        Channel session = RequireChannel(new(packet.AsMemory(1)));
        await session.CloseInputAsync().ConfigureAwait(false);
    }

    /// <summary>Returns true when the connection is over: both sides have closed the channel.</summary>
    private async Task<bool> OnChannelCloseAsync(byte[] packet, CancellationToken token)
    {
        Channel session = RequireChannel(new(packet.AsMemory(1)));
        session.CloseReceived = true;
        await session.CloseInputAsync().ConfigureAwait(false);
        if (!session.Started)
        {
            await session.SendCloseAsync(token).ConfigureAwait(false);
            return true;
        }
        if (session.CloseSent) return true;
        // The client gave up before the command finished. The command is cancelled; its completion sends
        // the close that ends the connection.
        session.Cancel();
        return false;
    }

    private async Task EndChannelAsync()
    {
        if (channel is null) return;
        channel.Cancel();
        await channel.CloseInputAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The session channel: the client's window on us and ours on it, the command's input as a pipe the
    /// reader fills, and the command's output as a stream that waits for window.
    /// </summary>
    private sealed class Channel
    {
        private readonly SshConnection connection;
        private readonly CancellationTokenSource commandLife;
        private readonly object gate = new();
        private long remoteWindow;
        private TaskCompletionSource windowOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private uint consumedSinceReplenish;
        private bool eofSent;

        public Channel(SshConnection connection, uint remoteId, uint remoteWindow, int remoteMaximumPacket)
        {
            this.connection = connection;
            RemoteId = remoteId;
            this.remoteWindow = remoteWindow;
            RemoteMaximumPacket = remoteMaximumPacket;
            commandLife = CancellationTokenSource.CreateLinkedTokenSource(connection.closing.Token);
            Output = new OutputStream(this);
        }

        public uint RemoteId { get; }
        public int RemoteMaximumPacket { get; }
        public uint LocalWindow { get; set; } = LocalWindowSize;
        public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);
        public Pipe Input { get; } = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        public Stream Output { get; }
        public bool Started { get; private set; }
        public bool InputClosed { get; private set; }
        public bool CloseReceived { get; set; }
        public bool CloseSent { get; private set; }
        public CancellationToken Token => commandLife.Token;

        public SshCommand CommandFor(object principal, string username, string command) =>
            new(principal, username, command, Environment, new InputStream(this), Output,
                (text, token) => connection.SendAsync(new SshWriter().Byte(ChannelExtendedData).UInt32(RemoteId).UInt32(1)
                    .String(Encoding.UTF8.GetBytes(text + "\n")).ToArray(), token));

        public void Start(Func<Task<int>> run)
        {
            Started = true;
            _ = RunAsync(run);
        }

        private async Task RunAsync(Func<Task<int>> run)
        {
            int status;
            try { status = await run().ConfigureAwait(false); }
            catch (OperationCanceledException) { status = 130; }
            catch (SshProtocolException) { status = 1; }
            catch (IOException) { status = 1; }
            catch (Exception exception)
            {
                connection.options.Log?.Invoke("ssh " + connection.peer + ": command failed", exception);
                status = 1;
                try
                {
                    await connection.SendAsync(new SshWriter().Byte(ChannelExtendedData).UInt32(RemoteId).UInt32(1)
                        .String("fatal: Graft could not complete the command\n"u8).ToArray(), connection.closing.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
            try
            {
                CancellationToken token = connection.closing.Token;
                if (!eofSent)
                {
                    eofSent = true;
                    await connection.SendAsync(new SshWriter().Byte(ChannelEof).UInt32(RemoteId).ToArray(), token).ConfigureAwait(false);
                }
                await connection.SendAsync(new SshWriter().Byte(ChannelRequest).UInt32(RemoteId).String("exit-status").Boolean(false)
                    .UInt32((uint)status).ToArray(), token).ConfigureAwait(false);
                await SendCloseAsync(token).ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        public async Task SendCloseAsync(CancellationToken token)
        {
            if (CloseSent) return;
            CloseSent = true;
            await connection.SendAsync(new SshWriter().Byte(ChannelClose).UInt32(RemoteId).ToArray(), token).ConfigureAwait(false);
            // Whatever the client does next, this connection is over shortly; if it has already closed its
            // side, it is over now.
            if (CloseReceived) connection.closing.Cancel();
            else connection.closing.CancelAfter(TimeSpan.FromSeconds(10));
        }

        public void Cancel() => commandLife.Cancel();

        public async Task CloseInputAsync()
        {
            if (InputClosed) return;
            InputClosed = true;
            await Input.Writer.CompleteAsync().ConfigureAwait(false);
        }

        public void Grant(uint bytes)
        {
            lock (gate)
            {
                remoteWindow = Math.Min(remoteWindow + bytes, uint.MaxValue);
                if (remoteWindow > 0) windowOpened.TrySetResult();
            }
        }

        /// <summary>The command has consumed input; once enough has gone, the client is given more window.</summary>
        public async Task ConsumedAsync(int bytes, CancellationToken token)
        {
            uint grant;
            lock (gate)
            {
                consumedSinceReplenish += (uint)bytes;
                if (consumedSinceReplenish < WindowReplenishAt) return;
                grant = consumedSinceReplenish;
                consumedSinceReplenish = 0;
                LocalWindow += grant;
            }
            await connection.SendAsync(new SshWriter().Byte(ChannelWindowAdjust).UInt32(RemoteId).UInt32(grant).ToArray(), token).ConfigureAwait(false);
        }

        public async Task SendDataAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            while (data.Length > 0)
            {
                Task wait;
                int allowed;
                lock (gate)
                {
                    allowed = (int)Math.Min(remoteWindow, RemoteMaximumPacket - 64);
                    if (allowed <= 0 && windowOpened.Task.IsCompleted)
                        windowOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = windowOpened.Task;
                    if (allowed > 0)
                    {
                        allowed = Math.Min(allowed, data.Length);
                        remoteWindow -= allowed;
                    }
                }
                if (allowed <= 0)
                {
                    await wait.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }
                await connection.SendAsync(new SshWriter().Byte(ChannelData).UInt32(RemoteId).String(data.Span[..allowed]).ToArray(), token).ConfigureAwait(false);
                data = data[allowed..];
            }
        }

        /// <summary>Standard input: the pipe the reader fills, granting window as the command drains it.</summary>
        private sealed class InputStream(Channel channel) : Stream
        {
            private readonly Stream inner = channel.Input.Reader.AsStream();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0) await channel.ConsumedAsync(read, cancellationToken).ConfigureAwait(false);
                return read;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>Standard output: each write becomes channel data once the client has window for it.</summary>
        private sealed class OutputStream(Channel channel) : Stream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                new(channel.SendDataAsync(buffer, cancellationToken));
            public override void Write(byte[] buffer, int offset, int count) =>
                channel.SendDataAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
