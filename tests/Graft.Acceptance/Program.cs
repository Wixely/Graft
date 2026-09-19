using System.Security.Cryptography;
using Graft.Core;
using Graft.Storage;

Console.WriteLine("Graft MVP-04 accounts/projects/membership acceptance (2026-09-19)");
var assertions = 0;
string root = Path.Combine(Path.GetTempPath(), "graft-mvp04-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string databasePath = Path.Combine(root, "graft.db");

string adminPassword = Password();
string alicePassword = Password();
string bobPassword = Password();
string carolPassword = Password();
SessionGrant adminSession;
SessionGrant aliceSession;
SessionGrant bobSession;
AuthenticatedIdentity adminIdentity;
AuthenticatedIdentity aliceIdentity;
AuthenticatedIdentity bobIdentity;
UserAccount bob;
UserAccount carol;
ProjectInfo project;
TokenGrant fullToken;
TokenGrant readToken;

try
{
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        UserAccount admin = await store.BootstrapAdministratorAsync("admin", adminPassword);
        Check(admin.IsAdministrator, "bootstrap creates administrator");
        await Throws<InvalidOperationException>(() => store.BootstrapAdministratorAsync("second-admin", Password()), "bootstrap is one use");
        Check(await store.LoginAsync("admin", "incorrect-password-value") is null, "invalid login is rejected");
        adminSession = Required(await store.LoginAsync("admin", adminPassword), "admin login");
        adminIdentity = Required(await store.ResolveSessionAsync(adminSession.Secret), "admin session resolution");

        UserAccount alice = await store.CreateUserAsync(adminIdentity, "alice", alicePassword);
        bob = await store.CreateUserAsync(adminIdentity, "bob", bobPassword);
        carol = await store.CreateUserAsync(adminIdentity, "carol", carolPassword);
        aliceSession = Required(await store.LoginAsync("alice", alicePassword), "alice login");
        bobSession = Required(await store.LoginAsync("bob", bobPassword), "bob login");
        aliceIdentity = Required(await store.ResolveSessionAsync(aliceSession.Secret), "alice session resolution");
        bobIdentity = Required(await store.ResolveSessionAsync(bobSession.Secret), "bob session resolution");

        project = await store.CreateProjectAsync(aliceIdentity, "private-project", "Private project");
        Check((await store.GetPermissionAsync(null, project.Id, ProjectAction.ReadRest)).Allowed is false, "anonymous private project denial");
        foreach (ProjectAction action in Enum.GetValues<ProjectAction>())
            Check(!(await store.GetPermissionAsync(bobIdentity, project.Id, action)).Allowed, "non-member denied " + action);
        await Throws<UnauthorizedAccessException>(
            () => store.SetMembershipAsync(bobIdentity, project.Id, bob.Id, ProjectRole.Maintainer),
            "non-maintainer cannot grant membership");

        PermissionDecision owner = await store.GetPermissionAsync(aliceIdentity, project.Id, ProjectAction.ManageProject);
        PermissionDecision administrator = await store.GetPermissionAsync(adminIdentity, project.Id, ProjectAction.ManageProject);
        Check(owner.Allowed && owner.Source == "Owner", "owner permission explanation");
        Check(administrator.Allowed && administrator.Source == "Administrator", "administrator permission explanation");

        await store.SetMembershipAsync(aliceIdentity, project.Id, bob.Id, ProjectRole.Developer);
        foreach (ProjectAction action in new[] { ProjectAction.ReadRest, ProjectAction.BrowseRepository, ProjectAction.FetchRepository, ProjectAction.PushRepository })
        {
            PermissionDecision decision = await store.GetPermissionAsync(bobIdentity, project.Id, action);
            Check(decision.Allowed && decision.EffectiveRole == ProjectRole.Developer && decision.Source == "Direct membership",
                "developer allowed " + action);
        }
        Check(!(await store.GetPermissionAsync(bobIdentity, project.Id, ProjectAction.ManageProject)).Allowed, "developer cannot manage project");

        fullToken = await store.CreateTokenAsync(bobIdentity, "all-workflow-scopes",
            TokenScope.Api | TokenScope.ReadRepository | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));
        AuthenticatedIdentity fullTokenIdentity = Required(await store.ResolveTokenAsync(fullToken.Secret), "full token resolution");
        Check((await store.GetPermissionAsync(fullTokenIdentity, project.Id, ProjectAction.PushRepository)).Allowed,
            "role and token scope intersect for push");

        readToken = await store.CreateTokenAsync(bobIdentity, "read-api-only", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1));
        AuthenticatedIdentity readTokenIdentity = Required(await store.ResolveTokenAsync(readToken.Secret), "read token resolution");
        Check((await store.GetPermissionAsync(readTokenIdentity, project.Id, ProjectAction.ReadRest)).Allowed, "read_api permits REST read");
        Check(!(await store.GetPermissionAsync(readTokenIdentity, project.Id, ProjectAction.FetchRepository)).Allowed, "read_api cannot fetch repository");
        Check(!(await store.GetPermissionAsync(readTokenIdentity, project.Id, ProjectAction.PushRepository)).Allowed, "read_api cannot push repository");

        SessionGrant carolSession = Required(await store.LoginAsync("carol", carolPassword), "carol login");
        AuthenticatedIdentity carolIdentity = Required(await store.ResolveSessionAsync(carolSession.Secret), "carol session resolution");
        ProjectInfo otherProject = await store.CreateProjectAsync(carolIdentity, "other-project", "Other project");
        Check(!(await store.GetPermissionAsync(bobIdentity, otherProject.Id, ProjectAction.ReadRest)).Allowed, "cross-project identifier denial");

        Check(!await store.ContainsStoredSecretAsync(adminPassword), "administrator password is not stored raw");
        Check(!await store.ContainsStoredSecretAsync(bobSession.Secret), "session is not stored raw");
        Check(!await store.ContainsStoredSecretAsync(fullToken.Secret), "token is not stored raw");
    }

    await using (GraftStore restarted = await GraftStore.OpenAsync(databasePath))
    {
        aliceIdentity = Required(await restarted.ResolveSessionAsync(aliceSession.Secret), "session survives restart");
        bobIdentity = Required(await restarted.ResolveSessionAsync(bobSession.Secret), "member session survives restart");
        AuthenticatedIdentity fullTokenIdentity = Required(await restarted.ResolveTokenAsync(fullToken.Secret), "token survives restart");
        Check((await restarted.GetPermissionAsync(bobIdentity, project.Id, ProjectAction.PushRepository)).Allowed,
            "membership survives restart");

        await restarted.RevokeMembershipAsync(aliceIdentity, project.Id, bob.Id);
        foreach (ProjectAction action in Enum.GetValues<ProjectAction>())
        {
            Check(!(await restarted.GetPermissionAsync(bobIdentity, project.Id, action)).Allowed,
                "revoked member session denied " + action);
            Check(!(await restarted.GetPermissionAsync(fullTokenIdentity, project.Id, action)).Allowed,
                "revoked member token denied " + action);
        }

        await restarted.SetMembershipAsync(aliceIdentity, project.Id, bob.Id, ProjectRole.Reporter);
        foreach (ProjectAction action in new[] { ProjectAction.ReadRest, ProjectAction.BrowseRepository, ProjectAction.FetchRepository })
            Check((await restarted.GetPermissionAsync(bobIdentity, project.Id, action)).Allowed, "reporter allowed " + action);
        Check(!(await restarted.GetPermissionAsync(bobIdentity, project.Id, ProjectAction.PushRepository)).Allowed,
            "reporter cannot push");

        await restarted.RevokeTokenAsync(bobIdentity, readToken.Id);
        Check(await restarted.ResolveTokenAsync(readToken.Secret) is null, "token revocation is immediate");
        await restarted.LogoutAsync(bobSession.Secret);
        Check(await restarted.ResolveSessionAsync(bobSession.Secret) is null, "logout revokes session");

        SessionGrant replacementSession = Required(await restarted.LoginAsync("bob", bobPassword), "bob relogin");
        AuthenticatedIdentity replacementIdentity = Required(await restarted.ResolveSessionAsync(replacementSession.Secret), "replacement session");
        TokenGrant replacementToken = await restarted.CreateTokenAsync(replacementIdentity, "disable-check", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1));
        adminIdentity = Required(await restarted.ResolveSessionAsync(adminSession.Secret), "admin session survives restart");
        await restarted.DisableUserAsync(adminIdentity, bob.Id);
        Check(await restarted.ResolveSessionAsync(replacementSession.Secret) is null, "disabled user session rejected");
        Check(await restarted.ResolveTokenAsync(replacementToken.Secret) is null, "disabled user token rejected");
        Check(!(await restarted.GetPermissionAsync(replacementIdentity, project.Id, ProjectAction.ReadRest)).Allowed,
            "stale disabled identity denied by authorization");
        Check(await restarted.LoginAsync("bob", bobPassword) is null, "disabled user cannot log in");

        Check((await restarted.GetPermissionAsync(aliceIdentity, project.Id, ProjectAction.ManageProject)).Allowed,
            "owner retains access after restart and unrelated disablement");
        await Throws<InvalidOperationException>(() => restarted.BootstrapAdministratorAsync("later-admin", Password()),
            "bootstrap remains closed after restart");
        Check(await restarted.CountAuditEventsAsync() >= 12, "security and membership changes are audited");
        await restarted.LogoutAsync(aliceSession.Secret);
        Check(await restarted.ResolveSessionAsync(aliceSession.Secret) is null, "owner logout revokes session");
    }

    Console.WriteLine($"RESULT: PASS; assertions: {assertions}");
    Console.WriteLine("BOUNDARY: shared authorization decisions are exercised for REST, browse, fetch and push capabilities; actual HTTP/Git transport is MVP-05.");
    return 0;
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); }
    catch { Console.WriteLine("WARN: temporary acceptance directory could not be removed."); }
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Acceptance assertion failed: " + name);
    assertions++;
}

T Required<T>(T? value, string name) where T : class
{
    Check(value is not null, name);
    return value!;
}

async Task Throws<TException>(Func<Task> action, string name) where TException : Exception
{
    try { await action(); }
    catch (TException) { assertions++; return; }
    throw new InvalidOperationException("Expected " + typeof(TException).Name + ": " + name);
}

static string Password() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
