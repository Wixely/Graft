using System.Text.Json.Serialization;

namespace Graft.Contracts;

public sealed record UserResponse(
    long Id,
    string Username,
    string Name,
    string State,
    bool Locked,
    [property: JsonPropertyName("web_url")] string WebUrl,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

public sealed record NamespaceResponse(
    long Id,
    string Name,
    string Path,
    string Kind,
    [property: JsonPropertyName("full_path")] string FullPath,
    [property: JsonPropertyName("web_url")] string WebUrl);

public sealed record ProjectResponse(
    long Id,
    string Description,
    string Name,
    [property: JsonPropertyName("name_with_namespace")] string NameWithNamespace,
    string Path,
    [property: JsonPropertyName("path_with_namespace")] string PathWithNamespace,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    string Visibility,
    [property: JsonPropertyName("ssh_url_to_repo")] string? SshUrlToRepo,
    [property: JsonPropertyName("http_url_to_repo")] string HttpUrlToRepo,
    [property: JsonPropertyName("web_url")] string WebUrl,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    NamespaceResponse Namespace);

public sealed record CommitResponse(
    string Id,
    [property: JsonPropertyName("short_id")] string ShortId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("parent_ids")] IReadOnlyList<string> ParentIds,
    string Title,
    string Message,
    [property: JsonPropertyName("author_name")] string AuthorName,
    [property: JsonPropertyName("author_email")] string AuthorEmail,
    [property: JsonPropertyName("authored_date")] DateTimeOffset AuthoredDate,
    [property: JsonPropertyName("committer_name")] string CommitterName,
    [property: JsonPropertyName("committer_email")] string CommitterEmail,
    [property: JsonPropertyName("committed_date")] DateTimeOffset CommittedDate,
    [property: JsonPropertyName("web_url")] string WebUrl);

public sealed record BranchResponse(
    string Name,
    bool Merged,
    bool Protected,
    bool Default,
    [property: JsonPropertyName("developers_can_push")] bool DevelopersCanPush,
    [property: JsonPropertyName("developers_can_merge")] bool DevelopersCanMerge,
    [property: JsonPropertyName("can_push")] bool CanPush,
    [property: JsonPropertyName("web_url")] string WebUrl,
    CommitResponse Commit);

public sealed record TreeEntryResponse(
    string Id,
    string Name,
    string Type,
    string Path,
    string Mode);

public sealed record FileResponse(
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("file_path")] string FilePath,
    int Size,
    string Encoding,
    string Content,
    [property: JsonPropertyName("content_sha256")] string ContentSha256,
    string Ref,
    [property: JsonPropertyName("blob_id")] string BlobId,
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("last_commit_id")] string LastCommitId,
    [property: JsonPropertyName("execute_filemode")] bool ExecuteFilemode);

public sealed record ErrorResponse(string Message);
public sealed record SessionResponse(
    bool Authenticated,
    string? Username,
    bool IsAdministrator,
    string Csrf,
    long UserId,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);
public sealed record LoginRequest(string Username, string Password);
public sealed record UserCreateRequest(string Username, string Password);
public sealed record UserAccountResponse(long Id, string Username, bool IsAdministrator, bool Disabled);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
public sealed record PasswordResetRequest(string NewPassword);
public sealed record TokenCreateRequest(string Name, string[] Scopes, int ExpiresInDays);
public sealed record TokenCreatedResponse(long Id, string Name, string Secret, string[] Scopes, DateTimeOffset ExpiresAtUtc);
public sealed record TokenResponse(long Id, string Name, string[] Scopes, DateTimeOffset ExpiresAtUtc, DateTimeOffset CreatedAtUtc);
public sealed record ProjectCreateRequest(string? Name, string? Path, string? Description, string? Visibility);
public sealed record MembershipSetRequest(string Username, string Role);

/// <summary>Names the account a project should be handed to. The project path changes with its owner.</summary>
public sealed record OwnershipTransferRequest(string Username);

/// <summary>Names the branch a project should open at. Null or empty clears the preference and lets the
/// repository fall back to convention.</summary>
public sealed record DefaultBranchRequest(string? Branch);
public sealed record ProjectMemberResponse(long UserId, string Username, string Role);
/// <summary>
/// Names a completed browser upload for an application operation to ingest. The upload id is a transport
/// receipt, not an authorization: the server re-checks the caller's permission and that the caller owns it.
/// </summary>
public sealed record UploadIngestRequest(string UploadId);

public sealed record ImportRefResponse(string Name, [property: JsonPropertyName("object_id")] string ObjectId);
public sealed record ImportExclusionResponse(string Area, string Reason);

/// <summary>What an uploaded Git bundle contains, and what it explicitly does not carry across.</summary>
public sealed record BundleInventoryResponse(
    string FileName,
    string Format,
    string Sha256,
    long Bytes,
    ImportRefResponse[] Refs,
    ImportExclusionResponse[] Excluded);

/// <summary>One uploaded file and where it should land in the repository.</summary>
public sealed record RepositoryFileUpload(
    [property: JsonPropertyName("upload_id")] string UploadId,
    string Path,
    bool Executable);

public sealed record RepositoryCommitRequest(string Branch, string Message, RepositoryFileUpload[] Files);

public sealed record RepositoryCommitResponse(
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("tree_id")] string TreeId,
    string Branch,
    [property: JsonPropertyName("created_branch")] bool CreatedBranch);

/// <summary>
/// A DnaX upload session as the server reports it. This is the authoritative reconciliation path: the
/// browser completion event is only a hint, so the page reads the session back before acting on it.
/// DnaX serializes camelCase, hence the explicit name.
/// </summary>
public sealed record UploadStatusResponse(
    string Id,
    string Profile,
    [property: JsonPropertyName("fileName")] string FileName,
    long Length,
    long Offset,
    string State);

public sealed record RepositoryPageResponse(
    ProjectResponse Project,
    bool CanManage,
    bool CanPush,
    IReadOnlyList<BranchResponse> Branches,
    IReadOnlyList<CommitResponse> Commits,
    IReadOnlyList<TreeEntryResponse> Tree,
    FileResponse? File);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(ProjectResponse))]
[JsonSerializable(typeof(ProjectResponse[]))]
[JsonSerializable(typeof(BranchResponse[]))]
[JsonSerializable(typeof(CommitResponse[]))]
[JsonSerializable(typeof(TreeEntryResponse[]))]
[JsonSerializable(typeof(FileResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(UserCreateRequest))]
[JsonSerializable(typeof(UserAccountResponse))]
[JsonSerializable(typeof(UserAccountResponse[]))]
[JsonSerializable(typeof(PasswordChangeRequest))]
[JsonSerializable(typeof(PasswordResetRequest))]
[JsonSerializable(typeof(TokenCreateRequest))]
[JsonSerializable(typeof(TokenCreatedResponse))]
[JsonSerializable(typeof(TokenResponse[]))]
[JsonSerializable(typeof(ProjectCreateRequest))]
[JsonSerializable(typeof(MembershipSetRequest))]
[JsonSerializable(typeof(OwnershipTransferRequest))]
[JsonSerializable(typeof(DefaultBranchRequest))]
[JsonSerializable(typeof(ProjectMemberResponse[]))]
[JsonSerializable(typeof(RepositoryPageResponse))]
[JsonSerializable(typeof(UploadIngestRequest))]
[JsonSerializable(typeof(BundleInventoryResponse))]
[JsonSerializable(typeof(RepositoryCommitRequest))]
[JsonSerializable(typeof(RepositoryCommitResponse))]
[JsonSerializable(typeof(UploadStatusResponse))]
public partial class ApiJson : JsonSerializerContext;
