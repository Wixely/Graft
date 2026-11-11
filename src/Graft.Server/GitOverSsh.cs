using System.Text;
using System.Text.RegularExpressions;
using Graft.Core;
using Graft.Git;
using Graft.Ssh;
using Graft.Storage;

namespace Graft.Server;

/// <summary>
/// Git over SSH: what happens on an SSH connection once the transport has authenticated a key.
///
/// <para>
/// A key resolves to an account, the account's role decides what it may do to the project the command
/// names, and then the same engine that answers smart HTTP answers the channel. The protocol is the one Git
/// speaks to <c>git-upload-pack</c> and <c>git-receive-pack</c> on a remote shell, with one bound: fetching
/// is served in protocol version 2 only, because the version 0 negotiation over a stateful connection is a
/// different conversation from the stateless one the engine implements, and every Git since 2.26 asks for
/// version 2 by default. A client that does not is told so by name.
/// </para>
/// </summary>
internal sealed partial class GitSshHost(
    GraftStore store,
    bool publicBrowsing,
    string globalNamespace,
    Func<ProjectInfo, RepositoryEndpoint> endpointFor,
    Func<ProjectInfo, AuthenticatedIdentity, List<(string Branch, string? From, string? To)>, Task> pushed,
    Func<string?> httpsOrigin,
    LfsTickets lfsTickets,
    Action<string, Exception?> log) : ISshHost
{
    /// <summary>How long a command waits for a repository another request holds before giving up.</summary>
    private static readonly TimeSpan RepositoryWait = TimeSpan.FromSeconds(60);

    [GeneratedRegex(@"^(git[- ](upload-pack|receive-pack))\s+'?([^'\s]+)'?\s*$")]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"^git-lfs-authenticate\s+'?([^'\s]+)'?\s+(upload|download)\s*$")]
    private static partial Regex LfsAuthenticatePattern();

    public async ValueTask<object?> AuthenticateAsync(string username, SshPublicKey key, bool signatureVerified, CancellationToken cancellationToken) =>
        await ResolvePrincipalAsync(key.Fingerprint, signatureVerified, cancellationToken);

    /// <summary>
    /// Who a key is: an account, when an account registered it, or a deploy key, when some project admits it and no
    /// account holds it. A deploy key's projects are decided per command, because which repository it is for is not
    /// known until the client names one.
    /// </summary>
    public async Task<SshPrincipal?> ResolvePrincipalAsync(string fingerprint, bool recordUse, CancellationToken cancellationToken)
    {
        AuthenticatedIdentity? account = await store.ResolveSshKeyAsync(fingerprint, recordUse, cancellationToken);
        if (account is not null) return new(fingerprint, account);
        return await store.IsDeployKeyAsync(fingerprint, cancellationToken) ? new(fingerprint, null) : null;
    }

    public string? Greeting(object principal, string username) => ((SshPrincipal)principal).Account is AuthenticatedIdentity account
        ? "Welcome to Graft, " + account.Username + ". This server serves Git only; there is no shell."
        : "Welcome to Graft. This is a deploy key; it serves Git for the projects that admit it, and there is no shell.";

    /// <summary>
    /// Whether this key may do this to this project, and the account the act is attributed to. An account's key acts
    /// with the account's role, as over HTTPS; a deploy key reads a project that admits it, and pushes only where the
    /// project said it may. Null for no, whatever the reason.
    /// </summary>
    public async Task<AuthenticatedIdentity?> AuthorizeAsync(SshPrincipal principal, ProjectInfo project, ProjectAction action, bool recordUse,
        CancellationToken cancellationToken)
    {
        if (principal.Account is AuthenticatedIdentity account)
        {
            PermissionDecision decision = await store.GetPermissionAsync(account, project.Id, action, cancellationToken);
            if (!publicBrowsing && decision.Grant is not (AccessGrant.Member or AccessGrant.None)) return null;
            return decision.Allowed ? account : null;
        }
        if (action is not (ProjectAction.FetchRepository or ProjectAction.PushRepository)) return null;
        DeployKeyGrant? grant = await store.FindDeployKeyGrantAsync(principal.Fingerprint, project.Id, recordUse, cancellationToken);
        if (grant is null || (action == ProjectAction.PushRepository && !grant.CanPush)) return null;
        return grant.AttributedTo;
    }

    public async Task<int> ExecuteAsync(SshCommand command, CancellationToken cancellationToken)
    {
        SshPrincipal principal = (SshPrincipal)command.Principal;
        Match lfs = LfsAuthenticatePattern().Match(command.CommandLine);
        if (lfs.Success) return await LfsAuthenticateAsync(command, principal, lfs.Groups[1].Value, lfs.Groups[2].Value == "upload", cancellationToken);
        Match match = CommandPattern().Match(command.CommandLine);
        if (!match.Success)
        {
            await command.WriteErrorAsync("fatal: Graft serves git-upload-pack, git-receive-pack and git-lfs-authenticate over SSH and nothing else.", cancellationToken);
            return 128;
        }
        string service = "git-" + match.Groups[2].Value;
        ProjectAction action = service == "git-receive-pack" ? ProjectAction.PushRepository : ProjectAction.FetchRepository;

        ProjectInfo? project = await ResolveAsync(match.Groups[3].Value, cancellationToken);
        AuthenticatedIdentity? identity = project is null ? null : await AuthorizeAsync(principal, project, action, recordUse: true, cancellationToken);
        if (project is null || identity is null)
        {
            // One answer for absent, private and not-yours, as the HTTP transport gives, so the path space
            // cannot be mapped by somebody who holds a key.
            await command.WriteErrorAsync("fatal: repository not found or you do not have " +
                (action == ProjectAction.PushRepository ? "push" : "read") + " access: " + match.Groups[3].Value, cancellationToken);
            return 128;
        }

        RepositoryEndpoint endpoint = endpointFor(project);
        try
        {
            return service == "git-receive-pack"
                ? await ReceiveAsync(command, identity, project, endpoint, cancellationToken)
                : await UploadAsync(command, endpoint, cancellationToken);
        }
        catch (RepositoryBusyException)
        {
            await command.WriteErrorAsync("fatal: the repository is busy; try again shortly.", cancellationToken);
            return 1;
        }
        catch (GitInputException exception)
        {
            await command.WriteErrorAsync("fatal: " + exception.Message, cancellationToken);
            return 128;
        }
    }

    /// <summary>
    /// Holds the repository for one operation, as an HTTP request does, never for a whole connection. A push's
    /// connection stays open while the client runs its pre-push hook, and git-lfs's hook opens a second connection to
    /// the same repository to list its branches; holding the repository across the first would make the second wait
    /// for it, and the push would stall until it gave up.
    /// </summary>
    private static async Task<T> WithRepositoryAsync<T>(RepositoryEndpoint endpoint, Func<Repository, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (!await endpoint.Gate.WaitAsync(RepositoryWait, cancellationToken)) throw new RepositoryBusyException();
        try { return await operation(endpoint.Repository); }
        finally { endpoint.Gate.Release(); }
    }

    private sealed class RepositoryBusyException : Exception;

    /// <summary>
    /// Large files over an SSH remote (ADR 0049). git-lfs asks the SSH server where the HTTPS large-file API is and for a
    /// credential to use there, then moves the objects over HTTPS. The answer is the repository's own large-file address
    /// and a ticket naming this key, this project and this operation, good for half an hour. The same refusal as any
    /// other command when the key may not do the operation, so the path space cannot be probed this way either.
    /// </summary>
    private async Task<int> LfsAuthenticateAsync(SshCommand command, SshPrincipal principal, string path, bool upload, CancellationToken cancellationToken)
    {
        ProjectInfo? project = await ResolveAsync(path, cancellationToken);
        AuthenticatedIdentity? identity = project is null ? null
            : await AuthorizeAsync(principal, project, upload ? ProjectAction.PushRepository : ProjectAction.FetchRepository, recordUse: true, cancellationToken);
        if (project is null || identity is null)
        {
            await command.WriteErrorAsync("fatal: repository not found or you do not have " + (upload ? "push" : "read") + " access: " + path, cancellationToken);
            return 128;
        }
        string? origin = httpsOrigin();
        if (origin is null)
        {
            await command.WriteErrorAsync("fatal: this server has no HTTPS address to send large files to yet; try again shortly.", cancellationToken);
            return 1;
        }

        string href = origin.TrimEnd('/') + "/" + string.Join('/', project.Namespace.Split('/').Select(Uri.EscapeDataString)) + "/" +
            Uri.EscapeDataString(project.Slug) + ".git/info/lfs";
        using MemoryStream body = new();
        using (System.Text.Json.Utf8JsonWriter json = new(body))
        {
            json.WriteStartObject();
            json.WriteString("href", href);
            json.WriteStartObject("header");
            json.WriteString("Authorization", LfsTickets.Scheme + " " + lfsTickets.Issue(principal.Fingerprint, project.Id, upload, DateTimeOffset.UtcNow));
            json.WriteEndObject();
            json.WriteNumber("expires_in", (long)LfsTickets.Lifetime.TotalSeconds);
            json.WriteEndObject();
        }
        body.WriteByte((byte)'\n');
        await command.Output.WriteAsync(body.ToArray(), cancellationToken);
        return 0;
    }

    /// <summary>
    /// <c>/owner/project.git</c>, <c>owner/project</c>, or <c>/projects/slug.git</c>: the same two paths the
    /// HTTP transport answers, with the same meaning.
    /// </summary>
    public async Task<ProjectInfo?> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        string trimmed = path.Trim('/');
        if (trimmed.EndsWith(".git", StringComparison.Ordinal)) trimmed = trimmed[..^4];
        string[] segments = trimmed.Split('/');
        // The last segment is the project; everything before it is the namespace, an account or a group's
        // full path, which may itself have several segments.
        if (segments.Length < 2 || segments.Any(segment => segment.Length == 0)) return null;
        string ns = string.Join('/', segments[..^1]);
        return segments.Length == 2 && string.Equals(ns, globalNamespace, StringComparison.OrdinalIgnoreCase)
            ? await store.FindReadyProjectByGlobalSlugAsync(segments[1], cancellationToken)
            : await store.FindReadyProjectAsync(ns, segments[^1], cancellationToken);
    }

    /// <summary>Protocol version 2 upload-pack: the capability list, then one command per request until EOF.</summary>
    private static async Task<int> UploadAsync(SshCommand command, RepositoryEndpoint endpoint, CancellationToken cancellationToken)
    {
        bool version2 = command.Environment.TryGetValue("GIT_PROTOCOL", out string? protocol) &&
            protocol.Split(':').Any(part => part.Trim() == "version=2");
        if (!version2)
        {
            await command.WriteErrorAsync("fatal: Graft serves fetches over SSH with Git protocol version 2 only. " +
                "Git 2.26 or later asks for it by default; older clients can run with -c protocol.version=2.", cancellationToken);
            return 128;
        }
        await command.Output.WriteAsync(await WithRepositoryAsync(endpoint, held => Task.FromResult(held.AdvertiseV2("git-upload-pack", serviceHeader: false)), cancellationToken),
            cancellationToken);
        while (true)
        {
            byte[]? request = await ReadPacketSectionAsync(command.Input, 16 * 1024 * 1024, cancellationToken);
            if (request is null) return 0;
            using MemoryStream input = new(request, false);
            using TemporaryStorage staging = new();
            using FileStream output = staging.CreateFile();
            // The answer is staged whole while the repository is held, then sent without it, so a slow reader holds
            // nothing but its own connection.
            await WithRepositoryAsync(endpoint, held => { held.ServeV2(input, output, cancellationToken); return Task.FromResult(0); }, cancellationToken);
            output.Position = 0;
            await output.CopyToAsync(command.Output, cancellationToken);
        }
    }

    /// <summary>Protocol version 0 receive-pack, which is the only one there is: advertise, read the
    /// commands and the pack, apply, report.</summary>
    private async Task<int> ReceiveAsync(SshCommand command, AuthenticatedIdentity identity, ProjectInfo project, RepositoryEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        // Released between the advertisement and the update: the client runs its hooks in between, and the update
        // checks each reference's old value itself, so a change made meanwhile is refused rather than overwritten.
        byte[] advertisement = await WithRepositoryAsync(endpoint, held => Task.FromResult(held.Advertise("git-receive-pack", serviceHeader: false)), cancellationToken);
        await command.Output.WriteAsync(advertisement, cancellationToken);
        byte[]? commands = await ReadPacketSectionAsync(command.Input, 1024 * 1024, cancellationToken);
        // A client with nothing to push sends a flush and goes away.
        if (commands is null || commands.Length <= 4) return 0;

        // A pack follows unless every command is a deletion, which must reach the engine rather than leave
        // this waiting for a pack that is not coming.
        bool expectPack = false;
        foreach (string line in PacketLines(commands))
        {
            string[] parts = line.Split('\0')[0].TrimEnd('\n').Split(' ');
            if (parts.Length == 3 && parts[1] != Limits.Zero) expectPack = true;
        }

        using TemporaryStorage staging = new();
        using FileStream request = staging.CreateFile();
        await request.WriteAsync(commands, cancellationToken);
        if (expectPack) Pack.Capture(command.Input, request, cancellationToken);
        request.Position = 0;

        return await WithRepositoryAsync(endpoint, async repository =>
        {
            Dictionary<string, string> before = new(repository.Refs, StringComparer.Ordinal);
            byte[] report = repository.Receive(request, cancellationToken);
            await command.Output.WriteAsync(report, cancellationToken);
            // Created, moved and deleted branches alike; a deleted one reports no new commit, as over HTTP.
            List<(string Branch, string? From, string? To)> moved = [];
            foreach (string name in before.Keys.Union(repository.Refs.Keys, StringComparer.Ordinal))
            {
                if (!name.StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
                string? old = before.GetValueOrDefault(name);
                string? id = repository.Refs.GetValueOrDefault(name);
                if (old != id) moved.Add((name["refs/heads/".Length..], old, id));
            }
            if (moved.Count > 0)
            {
                try { await pushed(project, identity, moved); }
                catch (Exception exception) { log("ssh push notification failed", exception); }
            }
            return 0;
        }, cancellationToken);
    }

    /// <summary>
    /// Reads packet lines up to and including the next flush packet, returning the raw bytes, or null at a
    /// clean EOF before anything arrived. Bounded, because a client decides how much it sends.
    /// </summary>
    private static async Task<byte[]?> ReadPacketSectionAsync(Stream input, int limit, CancellationToken cancellationToken)
    {
        MemoryStream section = new();
        byte[] header = new byte[4];
        while (true)
        {
            int got = 0;
            while (got < 4)
            {
                int read = await input.ReadAsync(header.AsMemory(got, 4 - got), cancellationToken);
                if (read == 0)
                {
                    if (got == 0 && section.Length == 0) return null;
                    throw new GitInputException("truncated");
                }
                got += read;
            }
            section.Write(header);
            Limits.Require(int.TryParse(Encoding.ASCII.GetString(header), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out int length), "packet-header");
            if (length is 0 or 1 or 2)
            {
                if (length == 0) return section.ToArray();
                continue;
            }
            Limits.Require(length is >= 4 and <= 65520, "packet-length");
            Limits.Require(section.Length + length - 4 <= limit, "request-limit");
            byte[] payload = new byte[length - 4];
            try { await input.ReadExactlyAsync(payload, cancellationToken); }
            catch (EndOfStreamException) { throw new GitInputException("truncated"); }
            section.Write(payload);
        }
    }

    private static IEnumerable<string> PacketLines(byte[] section)
    {
        Cursor cursor = new(section);
        while (cursor.Remaining > 0)
        {
            string? line = cursor.Packet();
            if (line is null) yield break;
            yield return line;
        }
    }
}

/// <summary>Who an SSH connection authenticated as: the key, and the account it belongs to, or null for a deploy key.</summary>
internal sealed record SshPrincipal(string Fingerprint, AuthenticatedIdentity? Account);
