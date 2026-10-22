namespace Graft.Core;

public enum ProjectRole
{
    Reporter = 10,
    Developer = 20,
    Maintainer = 30
}

public enum ProjectAction
{
    ReadRest,
    BrowseRepository,
    FetchRepository,
    PushRepository,
    ManageProject,
    /// <summary>Read the published project page: the curated surface, not the repository. This is the only
    /// action a <see cref="AccessGrant.Page"/> grant can satisfy.</summary>
    ReadPage,
    /// <summary>Read the project's issues, merge requests and comments. Granted wherever the repository
    /// itself can be read, including anonymously on a public project: a forge whose conversation is private
    /// while its code is public has the two the wrong way round.</summary>
    ReadCollaboration,
    /// <summary>Open an issue or a merge request, comment on one, or change its state. Members only, and
    /// deliberately not granted by visibility: a public project that accepted anonymous writing would be a
    /// spam target, and Graft has no moderation for it to fall back on.</summary>
    WriteCollaboration
}

/// <summary>
/// How widely a project may be read. Stored, and set only by a Maintainer; this is the ceiling the request
/// calls final, and a project page cannot exceed it.
/// </summary>
public enum ProjectVisibility
{
    /// <summary>Owner, members and administrators only. The default, and what every project created before
    /// visibility existed became.</summary>
    Private = 0,
    /// <summary>Readable by anyone holding the path, but never listed or searchable. This is not a
    /// credential and it does not expire; it is a permanent property of the project.</summary>
    Unlisted = 1,
    /// <summary>Readable by anyone, and listed in the public browser when the operator has enabled it.</summary>
    Public = 2
}

/// <summary>
/// Which of Graft's four routes to a project a caller actually came through. A caller holds exactly one, and
/// the grant carries its own reach, so "repository settings take precedence over project page settings" is a
/// property of the resolution order rather than of any individual check.
///
/// <para>
/// The ordering matters and is relied upon: <see cref="Member"/> then <see cref="Repository"/> then
/// <see cref="Page"/>. Each is strictly broader than the next, and the first that applies wins, so consulting
/// a page can never widen what a caller already has.
/// </para>
/// </summary>
public enum AccessGrant
{
    /// <summary>Nothing. Concealed as a 404, identically to a project that does not exist.</summary>
    None = 0,
    /// <summary>Only the paths the published page names. No clone, no history, no diff, no branch list,
    /// because every one of those walks the object graph and a graph walk cannot honour a path allowlist.</summary>
    Page = 1,
    /// <summary>The whole repository, read-only, because its visibility is public or unlisted.</summary>
    Repository = 2,
    /// <summary>Everything the caller's project role allows, including writes.</summary>
    Member = 3
}

/// <summary>The stored facts about a project that bear on access, resolved in one query.</summary>
public sealed record ProjectAccess(
    long OwnerUserId,
    ProjectVisibility Visibility,
    bool PagePublished,
    ProjectRole? Membership);

[Flags]
public enum TokenScope
{
    None = 0,
    ReadApi = 1,
    Api = 2,
    ReadRepository = 4,
    WriteRepository = 8
}

public enum IdentityKind
{
    BrowserSession,
    PersonalAccessToken
}

public sealed record AuthenticatedIdentity(
    long UserId,
    string Username,
    bool IsAdministrator,
    IdentityKind Kind,
    TokenScope TokenScopes = TokenScope.None);

public sealed record PermissionDecision(
    bool Allowed,
    ProjectRole? EffectiveRole,
    string Source,
    string Explanation,
    AccessGrant Grant = AccessGrant.None);

public static class ProjectAuthorization
{
    public static PermissionDecision Decide(
        AuthenticatedIdentity? identity,
        ProjectAccess project,
        ProjectAction action)
    {
        ArgumentNullException.ThrowIfNull(project);

        // A member grant is the broadest, so it is resolved first and never falls through on a role or scope
        // refusal: a member who may not do this has been answered, and the public grant below would be a
        // second opinion on a question already decided.
        if (identity is not null)
        {
            PermissionDecision? member = DecideAsMember(identity, project, action);
            if (member is not null) return member;
        }

        // Everyone else is answered exactly as an anonymous caller would be, whether or not they presented a
        // credential. A token's scopes limit what that token may reach; they cannot subtract from what the
        // whole world can already read without one, and pretending otherwise would mean a request with a
        // narrow token saw less of a public project than the same request with no token at all.
        if (project.Visibility is ProjectVisibility.Public or ProjectVisibility.Unlisted && VisibilityGrants(action))
            return new(true, ProjectRole.Reporter, "Visibility",
                $"Project visibility {project.Visibility} grants anonymous {action}.", AccessGrant.Repository);

        if (project.PagePublished && action == ProjectAction.ReadPage)
            return new(true, ProjectRole.Reporter, "Project page",
                "A published project page grants its own curated surface and nothing else.", AccessGrant.Page);

        return identity is null
            ? Denied("Anonymous", "Authentication is required.")
            : Denied("None", "No project role grants access.");
    }

    /// <summary>
    /// The member chain: administrator, owner, then direct membership. Returns null when the caller holds no
    /// role at all, which is the one case that may fall through to a public grant.
    /// </summary>
    private static PermissionDecision? DecideAsMember(
        AuthenticatedIdentity identity,
        ProjectAccess project,
        ProjectAction action)
    {
        ProjectRole? role;
        string source;
        if (identity.IsAdministrator)
        {
            role = ProjectRole.Maintainer;
            source = "Administrator";
        }
        else if (identity.UserId == project.OwnerUserId)
        {
            role = ProjectRole.Maintainer;
            source = "Owner";
        }
        else
        {
            role = project.Membership;
            source = project.Membership is null ? "None" : "Direct membership";
        }

        if (role is null) return null;

        ProjectRole required = action switch
        {
            ProjectAction.PushRepository => ProjectRole.Developer,
            ProjectAction.ManageProject => ProjectRole.Maintainer,
            _ => ProjectRole.Reporter
        };
        if (role < required)
            return new(false, role, source, $"{source} role {role} does not grant {action}.");

        if (identity.Kind == IdentityKind.PersonalAccessToken && !ScopeAllows(identity.TokenScopes, action))
            return new(false, role, "Token scope", $"Token scopes do not grant {action}; project role cannot broaden a token.");

        return new(true, role, source, $"{source} grants {role} access for {action}.", AccessGrant.Member);
    }

    /// <summary>
    /// What a public or unlisted project grants a caller holding no role, which is every reading action and
    /// nothing else. Anonymous callers never write, never manage, and never participate.
    /// </summary>
    private static bool VisibilityGrants(ProjectAction action) => action
        is ProjectAction.ReadRest
        or ProjectAction.BrowseRepository
        or ProjectAction.FetchRepository
        or ProjectAction.ReadPage
        or ProjectAction.ReadCollaboration;

    private static bool ScopeAllows(TokenScope scopes, ProjectAction action) => action switch
    {
        ProjectAction.ReadRest or ProjectAction.ReadPage =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.ReadApi),
        ProjectAction.BrowseRepository =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.ReadApi) || scopes.HasFlag(TokenScope.ReadRepository),
        ProjectAction.FetchRepository =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.ReadRepository) || scopes.HasFlag(TokenScope.WriteRepository),
        ProjectAction.PushRepository =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.WriteRepository),
        ProjectAction.ManageProject => scopes.HasFlag(TokenScope.Api),
        // Collaboration is an API surface, so the repository scopes do not reach it in either direction. A
        // token minted to clone is not a token to read somebody's issues, and writing one needs the full
        // api scope because read_api is the read half by name.
        ProjectAction.ReadCollaboration =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.ReadApi),
        ProjectAction.WriteCollaboration => scopes.HasFlag(TokenScope.Api),
        _ => false
    };

    private static PermissionDecision Denied(string source, string explanation) => new(false, null, source, explanation);
}

public sealed record UserAccount(long Id, string Username, bool IsAdministrator, bool Disabled);

/// <summary>
/// One recorded act. The actor is nullable because the first administrator is created before any account
/// exists to have done it, and the username is nullable for the same reason rather than because accounts
/// can vanish: nothing deletes a user row.
///
/// <para>
/// An audit entry is a statement that something happened, and never a copy of what it happened to. No
/// field here holds a secret, a password or a token, and that is a property the writers maintain rather
/// than one this shape can enforce.
/// </para>
/// </summary>
public sealed record AuditEntry(
    long Id,
    long? ActorUserId,
    string? ActorUsername,
    string Action,
    string TargetType,
    string? TargetId,
    string Detail,
    DateTimeOffset CreatedAtUtc);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, long Total);
public sealed record ProjectMember(long UserId, string Username, ProjectRole Role);
public sealed record ProjectInfo(long Id, long OwnerUserId, string Namespace, string Slug, string Name, string StorageId,
    string? DefaultBranch = null,
    ProjectVisibility Visibility = ProjectVisibility.Private,
    bool PagePublished = false,
    /// <summary>The instance-wide name this project can also be reached by, without its owner in the path.
    /// Every stored project has one; it is empty only on a value built before the row was written.</summary>
    string GlobalSlug = "");
/// <summary>
/// A project that has just been deleted. Carries its storage directory because nothing else does any more:
/// the row naming it is gone, so a caller that still has to remove the repository on disk has only this.
/// </summary>
public sealed record DeletedProject(long Id, string Namespace, string Slug, string StorageId);
/// <summary>A project and its stored settings. DefaultBranch is a preference and may be null, or may name
/// a branch that no longer exists; the Git engine resolves the effective branch.</summary>
public sealed record ProjectDetails(ProjectInfo Project, string Description, DateTimeOffset CreatedAtUtc, string? DefaultBranch);
public sealed record ProjectPage(IReadOnlyList<ProjectDetails> Projects, long Total);
public sealed record SessionGrant(string Secret, DateTimeOffset ExpiresAtUtc);

/// <summary>A resolved browser session. <c>Renewed</c> means the stored expiry just moved, so the caller
/// owes the browser a refreshed cookie: the browser drops the cookie at the moment the server stops
/// honouring it, and the two disagreeing is how a live session turns into an anonymous request.</summary>
public sealed record ResolvedSession(AuthenticatedIdentity Identity, DateTimeOffset ExpiresAtUtc, bool Renewed);
public sealed record TokenGrant(long Id, string Secret, TokenScope Scopes, DateTimeOffset ExpiresAtUtc);
public sealed record PersonalAccessToken(long Id, string Name, TokenScope Scopes, DateTimeOffset ExpiresAtUtc, DateTimeOffset CreatedAtUtc,
    /// <summary>Whether this token's secret can still be produced. True only where an operator had asked
    /// for recoverable tokens when it was issued, so it is a property of the token and not of the server.</summary>
    bool Recoverable = false);

/// <summary>
/// The administrator's switches for the forge's own Model Context Protocol endpoint. These exist only
/// beneath the operator's: a server started without the endpoint has nothing for them to govern.
///
/// <para>
/// Enabled defaults open because the operator starting the endpoint is already the deliberate act; this
/// switch lets an administrator pause agent access without a redeploy. The other two default closed, the
/// same as the launched server's flags did: writes and anonymous callers are each a widening that has to be
/// asked for.
/// </para>
/// </summary>
public sealed record McpSettings(bool Enabled, bool AllowWrites, bool AllowAnonymous)
{
    public static McpSettings Default { get; } = new(true, false, false);
}
