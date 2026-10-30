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
    Action<string, Exception?> log) : ISshHost
{
    /// <summary>How long a command waits for a repository another request holds before giving up.</summary>
    private static readonly TimeSpan RepositoryWait = TimeSpan.FromSeconds(60);

    [GeneratedRegex(@"^(git[- ](upload-pack|receive-pack))\s+'?([^'\s]+)'?\s*$")]
    private static partial Regex CommandPattern();

    public async ValueTask<object?> AuthenticateAsync(string username, SshPublicKey key, bool signatureVerified, CancellationToken cancellationToken) =>
        await store.ResolveSshKeyAsync(key.Fingerprint, recordUse: signatureVerified, cancellationToken);

    public string? Greeting(object principal, string username) =>
        "Welcome to Graft, " + ((AuthenticatedIdentity)principal).Username + ". This server serves Git only; there is no shell.";

    public async Task<int> ExecuteAsync(SshCommand command, CancellationToken cancellationToken)
    {
        AuthenticatedIdentity identity = (AuthenticatedIdentity)command.Principal;
        Match match = CommandPattern().Match(command.CommandLine);
        if (!match.Success)
        {
            await command.WriteErrorAsync("fatal: Graft serves git-upload-pack and git-receive-pack over SSH and nothing else.", cancellationToken);
            return 128;
        }
        string service = "git-" + match.Groups[2].Value;
        ProjectAction action = service == "git-receive-pack" ? ProjectAction.PushRepository : ProjectAction.FetchRepository;

        ProjectInfo? project = await ResolveAsync(match.Groups[3].Value, cancellationToken);
        PermissionDecision? decision = project is null ? null : await store.GetPermissionAsync(identity, project.Id, action, cancellationToken);
        if (decision is not null && !publicBrowsing && decision.Grant is not (AccessGrant.Member or AccessGrant.None)) decision = null;
        if (project is null || decision is not { Allowed: true })
        {
            // One answer for absent, private and not-yours, as the HTTP transport gives, so the path space
            // cannot be mapped by somebody who holds a key.
            await command.WriteErrorAsync("fatal: repository not found or you do not have " +
                (action == ProjectAction.PushRepository ? "push" : "read") + " access: " + match.Groups[3].Value, cancellationToken);
            return 128;
        }

        RepositoryEndpoint endpoint = endpointFor(project);
        if (!await endpoint.Gate.WaitAsync(RepositoryWait, cancellationToken))
        {
            await command.WriteErrorAsync("fatal: the repository is busy; try again shortly.", cancellationToken);
            return 1;
        }
        try
        {
            return service == "git-receive-pack"
                ? await ReceiveAsync(command, identity, project, endpoint.Repository, cancellationToken)
                : await UploadAsync(command, endpoint.Repository, cancellationToken);
        }
        catch (GitInputException exception)
        {
            await command.WriteErrorAsync("fatal: " + exception.Message, cancellationToken);
            return 128;
        }
        finally { endpoint.Gate.Release(); }
    }

    /// <summary>
    /// <c>/owner/project.git</c>, <c>owner/project</c>, or <c>/projects/slug.git</c>: the same two paths the
    /// HTTP transport answers, with the same meaning.
    /// </summary>
    private async Task<ProjectInfo?> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        string trimmed = path.Trim('/');
        if (trimmed.EndsWith(".git", StringComparison.Ordinal)) trimmed = trimmed[..^4];
        string[] segments = trimmed.Split('/');
        if (segments.Length != 2 || segments.Any(segment => segment.Length == 0)) return null;
        return string.Equals(segments[0], globalNamespace, StringComparison.OrdinalIgnoreCase)
            ? await store.FindReadyProjectByGlobalSlugAsync(segments[1], cancellationToken)
            : await store.FindReadyProjectAsync(segments[0], segments[1], cancellationToken);
    }

    /// <summary>Protocol version 2 upload-pack: the capability list, then one command per request until EOF.</summary>
    private static async Task<int> UploadAsync(SshCommand command, Repository repository, CancellationToken cancellationToken)
    {
        bool version2 = command.Environment.TryGetValue("GIT_PROTOCOL", out string? protocol) &&
            protocol.Split(':').Any(part => part.Trim() == "version=2");
        if (!version2)
        {
            await command.WriteErrorAsync("fatal: Graft serves fetches over SSH with Git protocol version 2 only. " +
                "Git 2.26 or later asks for it by default; older clients can run with -c protocol.version=2.", cancellationToken);
            return 128;
        }
        await command.Output.WriteAsync(repository.AdvertiseV2("git-upload-pack", serviceHeader: false), cancellationToken);
        while (true)
        {
            byte[]? request = await ReadPacketSectionAsync(command.Input, 16 * 1024 * 1024, cancellationToken);
            if (request is null) return 0;
            using MemoryStream input = new(request, false);
            using TemporaryStorage staging = new();
            using FileStream output = staging.CreateFile();
            repository.ServeV2(input, output, cancellationToken);
            output.Position = 0;
            await output.CopyToAsync(command.Output, cancellationToken);
        }
    }

    /// <summary>Protocol version 0 receive-pack, which is the only one there is: advertise, read the
    /// commands and the pack, apply, report.</summary>
    private async Task<int> ReceiveAsync(SshCommand command, AuthenticatedIdentity identity, ProjectInfo project, Repository repository,
        CancellationToken cancellationToken)
    {
        await command.Output.WriteAsync(repository.Advertise("git-receive-pack", serviceHeader: false), cancellationToken);
        byte[]? commands = await ReadPacketSectionAsync(command.Input, 1024 * 1024, cancellationToken);
        // A client with nothing to push sends a flush and goes away.
        if (commands is null || commands.Length <= 4) return 0;

        // A pack follows unless every command is a deletion, which the engine refuses by name but which must
        // reach it rather than leave this waiting for a pack that is not coming.
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

        Dictionary<string, string> before = new(repository.Refs, StringComparer.Ordinal);
        byte[] report = repository.Receive(request, cancellationToken);
        await command.Output.WriteAsync(report, cancellationToken);
        List<(string Branch, string? From, string? To)> moved = [];
        foreach ((string name, string id) in repository.Refs)
        {
            if (!name.StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
            string? old = before.GetValueOrDefault(name);
            if (old != id) moved.Add((name["refs/heads/".Length..], old, id));
        }
        if (moved.Count > 0)
        {
            try { await pushed(project, identity, moved); }
            catch (Exception exception) { log("ssh push notification failed", exception); }
        }
        return 0;
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
