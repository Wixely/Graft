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
    ManageProject
}

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
    string Explanation);

public static class ProjectAuthorization
{
    public static PermissionDecision Decide(
        AuthenticatedIdentity? identity,
        long ownerUserId,
        ProjectRole? membership,
        ProjectAction action)
    {
        if (identity is null)
            return Denied("Anonymous", "Authentication is required.");

        ProjectRole? role;
        string source;
        if (identity.IsAdministrator)
        {
            role = ProjectRole.Maintainer;
            source = "Administrator";
        }
        else if (identity.UserId == ownerUserId)
        {
            role = ProjectRole.Maintainer;
            source = "Owner";
        }
        else
        {
            role = membership;
            source = membership is null ? "None" : "Direct membership";
        }

        if (role is null)
            return Denied(source, "No project role grants access.");

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

        return new(true, role, source, $"{source} grants {role} access for {action}.");
    }

    private static bool ScopeAllows(TokenScope scopes, ProjectAction action) => action switch
    {
        ProjectAction.ReadRest =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.ReadApi),
        ProjectAction.BrowseRepository =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.ReadApi) || scopes.HasFlag(TokenScope.ReadRepository),
        ProjectAction.FetchRepository =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.ReadRepository) || scopes.HasFlag(TokenScope.WriteRepository),
        ProjectAction.PushRepository =>
            scopes.HasFlag(TokenScope.Api) || scopes.HasFlag(TokenScope.WriteRepository),
        ProjectAction.ManageProject => scopes.HasFlag(TokenScope.Api),
        _ => false
    };

    private static PermissionDecision Denied(string source, string explanation) => new(false, null, source, explanation);
}

public sealed record UserAccount(long Id, string Username, bool IsAdministrator, bool Disabled);
public sealed record ProjectMember(long UserId, string Username, ProjectRole Role);
public sealed record ProjectInfo(long Id, long OwnerUserId, string Namespace, string Slug, string Name, string StorageId);
public sealed record ProjectDetails(ProjectInfo Project, string Description, DateTimeOffset CreatedAtUtc);
public sealed record ProjectPage(IReadOnlyList<ProjectDetails> Projects, long Total);
public sealed record SessionGrant(string Secret, DateTimeOffset ExpiresAtUtc);
public sealed record TokenGrant(long Id, string Secret, TokenScope Scopes, DateTimeOffset ExpiresAtUtc);
public sealed record PersonalAccessToken(long Id, string Name, TokenScope Scopes, DateTimeOffset ExpiresAtUtc, DateTimeOffset CreatedAtUtc);
