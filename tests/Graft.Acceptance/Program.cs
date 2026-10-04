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

    await using (GraftStore avatars = await GraftStore.OpenAsync(databasePath))
    {
        // Only structurally valid raster images are accepted. SVG is excluded on purpose: it can carry
        // script, and an avatar is served back to a browser.
        Check(ImageContent.Validate(Png(64, 48)) is { ContentType: "image/png", Width: 64, Height: 48 }, "a PNG is recognised");
        Check(ImageContent.Validate(Gif(12, 9)) is { ContentType: "image/gif", Width: 12, Height: 9 }, "a GIF is recognised");
        Check(ImageContent.Validate(Jpeg(200, 100)) is { ContentType: "image/jpeg", Width: 200, Height: 100 }, "a JPEG is recognised");
        Check(ImageContent.Validate("<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>"u8.ToArray()) is null, "SVG is refused");
        Check(ImageContent.Validate("not an image at all"u8.ToArray()) is null, "arbitrary text is refused");
        Check(ImageContent.Validate([]) is null, "an empty file is refused");
        Check(ImageContent.Validate(Png(ImageContent.MaximumDimension + 1, 10)) is null, "an oversized image is refused");
        Check(ImageContent.Validate(new byte[ImageContent.MaximumBytes + 1]) is null, "an overlong file is refused");
        // A PNG signature followed by rubbish must not be accepted on the strength of its header alone.
        Check(ImageContent.Validate([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]) is null, "a truncated PNG is refused");

        AuthenticatedIdentity avatarAdmin = Required(await avatars.ResolveSessionAsync(
            Required(await avatars.LoginAsync("admin", adminPassword), "admin re-login").Secret), "admin identity");
        AuthenticatedIdentity carolIdentity = Required(await avatars.ResolveSessionAsync(
            Required(await avatars.LoginAsync("carol", carolPassword), "carol login").Secret), "carol identity");

        // A real image is several hundred kilobytes. Dapper.AOT expands a byte array into one SQL variable
        // per element, so this previously failed with "too many SQL variables"; the write uses a provider
        // command for the BLOB instead. Keep an image large enough here to catch a regression.
        ValidatedImage large = Required(ImageContent.Validate(Png(1024, 1024, padding: 300_000)), "large image validates");
        Check(large.Bytes.Length > 250_000, "regression image is large enough to matter");
        await avatars.SetAvatarAsync(carolIdentity, AvatarOwner.User, carol.Id, large);
        StoredAvatar stored = Required(await avatars.GetAvatarAsync(AvatarOwner.User, carol.Id), "avatar reads back");
        Check(stored.Bytes.SequenceEqual(large.Bytes) && stored.ContentType == "image/png", "avatar bytes round-trip intact");
        Check(await avatars.HasAvatarAsync(AvatarOwner.User, carol.Id), "avatar presence is reported");
        Check(!await avatars.HasAvatarAsync(AvatarOwner.User, bob.Id), "an unset avatar is reported absent");

        ValidatedImage replacement = Required(ImageContent.Validate(Gif(8, 8)), "replacement validates");
        await avatars.SetAvatarAsync(carolIdentity, AvatarOwner.User, carol.Id, replacement);
        StoredAvatar replaced = Required(await avatars.GetAvatarAsync(AvatarOwner.User, carol.Id), "replaced avatar reads back");
        Check(replaced.ContentType == "image/gif" && replaced.Bytes.Length == replacement.Bytes.Length, "setting an avatar replaces the previous one");

        await Throws<UnauthorizedAccessException>(() => avatars.SetAvatarAsync(carolIdentity, AvatarOwner.User, bob.Id, replacement),
            "a user cannot set another user's avatar");
        await avatars.SetAvatarAsync(avatarAdmin, AvatarOwner.User, carol.Id, replacement);
        assertions++;
        await Throws<UnauthorizedAccessException>(() => avatars.SetAvatarAsync(carolIdentity, AvatarOwner.Project, project.Id, replacement),
            "a non-member cannot set a project avatar");
        Check(await avatars.GetAvatarAsync(AvatarOwner.Project, project.Id) is null, "a refused project avatar was not stored");
    }

    await using (GraftStore ownership = await GraftStore.OpenAsync(databasePath))
    {
        AuthenticatedIdentity owner = Required(await ownership.ResolveSessionAsync(
            Required(await ownership.LoginAsync("alice", alicePassword), "alice re-login").Secret), "alice identity");
        AuthenticatedIdentity administrator = Required(await ownership.ResolveSessionAsync(
            Required(await ownership.LoginAsync("admin", adminPassword), "admin re-login").Secret), "admin identity");
        AuthenticatedIdentity member = Required(await ownership.ResolveSessionAsync(
            Required(await ownership.LoginAsync("carol", carolPassword), "carol re-login").Secret), "carol identity");

        // A project has one owner and only that owner or an administrator can manage it, so disabling an
        // owner would leave it unmanageable by anyone else and unrecoverable: there would be no way to
        // appoint a replacement. The account must hand its projects over first.
        await Throws<ProjectOwnershipException>(() => ownership.DisableUserAsync(administrator, owner.UserId),
            "an account owning projects cannot be disabled");
        Check((await ownership.GetPermissionAsync(owner, project.Id, ProjectAction.ManageProject)).Allowed,
            "the refused disable left the owner intact");

        // A Maintainer membership grants management, not the right to give the project away.
        await ownership.SetMembershipAsync(owner, project.Id, carol.Id, ProjectRole.Maintainer);
        await Throws<UnauthorizedAccessException>(() => ownership.TransferProjectOwnershipAsync(member, project.Id, carol.Id),
            "a maintainer cannot transfer ownership");
        await Throws<InvalidOperationException>(() => ownership.TransferProjectOwnershipAsync(owner, project.Id, 999_999),
            "ownership cannot move to an unknown account");
        await Throws<InvalidOperationException>(() => ownership.TransferProjectOwnershipAsync(owner, project.Id, owner.UserId),
            "ownership cannot move to the current owner");

        await ownership.TransferProjectOwnershipAsync(owner, project.Id, carol.Id);
        assertions++;
        Check((await ownership.GetPermissionAsync(member, project.Id, ProjectAction.ManageProject)).Allowed,
            "the new owner can manage the project");
        PermissionDecision previous = await ownership.GetPermissionAsync(owner, project.Id, ProjectAction.ManageProject);
        Check(previous.Allowed && previous.Source == "Direct membership",
            "the previous owner keeps access as a member rather than losing the project");

        // The project moved namespace with its owner, because a namespace is its owner's username.
        Check(await ownership.FindReadyProjectAsync("alice", "private-project") is null, "the old project path stops resolving");
        Check(await ownership.FindReadyProjectAsync("carol", "private-project") is not null, "the project resolves under its new owner");

        // Transferring away unblocks the disable that was refused above.
        await ownership.DisableUserAsync(administrator, owner.UserId);
        assertions++;
        Check(await ownership.LoginAsync("alice", alicePassword) is null, "the former owner is disabled once it owns nothing");
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

// Minimal but structurally real fixtures. These exercise the container parsing the validator performs; they
// are not decodable images, which is exactly the boundary being tested: structure is checked, pixels are not.
static byte[] Png(int width, int height, int padding = 0)
{
    byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
    byte[] bytes = new byte[header.Length + 17 + padding];
    header.CopyTo(bytes, 0);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
    return bytes;
}

static byte[] Gif(int width, int height)
{
    byte[] bytes = new byte[14];
    "GIF89a"u8.CopyTo(bytes);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), (ushort)width);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)height);
    return bytes;
}

static byte[] Jpeg(int width, int height)
{
    // Start of image, then a baseline start-of-frame segment carrying the dimensions.
    byte[] bytes = new byte[20];
    bytes[0] = 0xFF; bytes[1] = 0xD8;
    bytes[2] = 0xFF; bytes[3] = 0xC0;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 11);
    bytes[6] = 8;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(7, 2), (ushort)height);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9, 2), (ushort)width);
    return bytes;
}
