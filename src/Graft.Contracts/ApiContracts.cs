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
    NamespaceResponse Namespace,
    /// <summary>The instance-wide name this project also answers to, with no owner in the path. Graft's
    /// own field: GitLab has no equivalent, and it is additive, so a client that ignores it loses
    /// nothing.</summary>
    [property: JsonPropertyName("global_slug")] string GlobalSlug = "",
    /// <summary>The shareable page for that slug, and <c>.git</c> after it is the clone URL.</summary>
    [property: JsonPropertyName("global_web_url")] string GlobalWebUrl = "");

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
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    /// <summary>Whether this server's operator permits publishing and serves a public listing. A property of
    /// the server, not of the caller, so it is reported to an anonymous caller as well.</summary>
    bool PublicBrowsing = false,
    /// <summary>Whether this server's operator started it with the Model Context Protocol endpoint available.
    /// Also a property of the server; the administrator settings behind it are read separately.</summary>
    bool McpAvailable = false);
public sealed record LoginRequest(string Username, string Password);
public sealed record UserCreateRequest(string Username, string Password);
public sealed record UserAccountResponse(long Id, string Username, bool IsAdministrator, bool Disabled);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
public sealed record PasswordResetRequest(string NewPassword);
public sealed record TokenCreateRequest(string Name, string[] Scopes, int ExpiresInDays);
/// <summary>
/// A token that was just created. It carries no secret: the secret is fetched once, on demand, with
/// <see cref="Reveal"/>, so a token nobody copies never reaches the browser at all and a copied one never
/// has to be put in the page to be copied.
/// </summary>
public sealed record TokenCreatedResponse(long Id, string Name, string Reveal, string[] Scopes, DateTimeOffset ExpiresAtUtc);

/// <summary>Claims the one-time secret of a token just created. The handle is single use and short lived.</summary>
public sealed record TokenRevealRequest(string Reveal);
public sealed record TokenRevealResponse(string Secret);
public sealed record TokenResponse(long Id, string Name, string[] Scopes, DateTimeOffset ExpiresAtUtc, DateTimeOffset CreatedAtUtc,
    /// <summary>Whether this token's secret can still be produced, which is true only where the operator had
    /// asked for recoverable tokens when it was issued.</summary>
    bool Recoverable = false);

/// <summary>Reads back an existing token's secret. The account password is required: a session alone must
/// not turn into every credential the account holds.</summary>
public sealed record TokenSecretRequest(string Password);
public sealed record ProjectCreateRequest(string? Name, string? Path, string? Description, string? Visibility);
public sealed record MembershipSetRequest(string Username, string Role);

/// <summary>A project's human-facing text. A null field is left alone, so one can be changed without
/// resending the other.</summary>
public sealed record ProjectDetailsRequest(string? Name, string? Description);

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

/// <summary>
/// One file written through the API, in GitLab's shape: the content travels in the request rather than as
/// an upload receipt, because [ADR 0005](../../docs/decisions/0005-browser-upload-transport.md) puts the
/// resumable transport in the browser and plain HTTP in front of API clients.
/// </summary>
public sealed record RepositoryFileWriteRequest(
    string? Branch,
    string? Content,
    /// <summary><c>text</c> or <c>base64</c>. Absent means text.</summary>
    string? Encoding,
    string? CommitMessage,
    bool ExecuteFilemode = false);

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

/// <summary>One line of a diff. <c>Kind</c> is " ", "+" or "-"; a line numbered on only one side is a
/// change on that side. <c>NoNewline</c> marks a line that ends its file without a trailing newline.</summary>
public sealed record DiffLineResponse(
    string Kind,
    string Text,
    [property: JsonPropertyName("old_line")] int? OldLine,
    [property: JsonPropertyName("new_line")] int? NewLine,
    [property: JsonPropertyName("no_newline")] bool NoNewline);

/// <summary>A run of changed lines with its context, carrying the header a unified diff would print.</summary>
public sealed record DiffHunkResponse(
    string Header,
    IReadOnlyList<DiffLineResponse> Lines);

/// <summary>
/// What happened to one path. <c>Hunks</c> is empty when there is no text to show, which covers a binary
/// file, a file too large to compare, and a change that only altered the file's mode.
/// </summary>
public sealed record DiffFileResponse(
    string Path,
    string Status,
    [property: JsonPropertyName("old_mode")] string? OldMode,
    [property: JsonPropertyName("new_mode")] string? NewMode,
    [property: JsonPropertyName("is_binary")] bool IsBinary,
    [property: JsonPropertyName("too_large")] bool TooLarge,
    int Added,
    int Removed,
    IReadOnlyList<DiffHunkResponse> Hunks);

/// <summary>A commit and what it changed, for the page a commit link opens.</summary>
public sealed record CommitDiffResponse(
    ProjectResponse Project,
    CommitResponse Commit,
    [property: JsonPropertyName("parent_id")] string? ParentId,
    IReadOnlyList<DiffFileResponse> Files,
    bool Truncated,
    int Added,
    int Removed);

/// <summary>A rendered README, with whether it contains diagrams the client must load a renderer for.</summary>
public sealed record ReadmeResponse(
    string Path,
    string Html,
    [property: JsonPropertyName("has_diagrams")] bool HasDiagrams);

public sealed record RepositoryPageResponse(
    ProjectResponse Project,
    bool CanManage,
    bool CanPush,
    IReadOnlyList<BranchResponse> Branches,
    IReadOnlyList<CommitResponse> Commits,
    IReadOnlyList<TreeEntryResponse> Tree,
    FileResponse? File,
    ReadmeResponse? Readme,
    /// <summary>Whether the curated project page is published. The repository's own visibility is reported
    /// by <see cref="ProjectResponse.Visibility"/>; these are separate settings, because a page may stand
    /// over a private repository.</summary>
    bool PagePublished,
    /// <summary>Whether this server's operator permits publishing at all. False hides the controls rather
    /// than offering a choice the server would refuse.</summary>
    bool PublishingAllowed,
    /// <summary>Whether this caller may open an issue, propose a merge or comment. Weaker than
    /// <see cref="CanPush"/>: a Reporter holds this and cannot push, so the two are reported separately
    /// rather than one standing in for the other.</summary>
    bool CanParticipate = false);

public sealed record VisibilityRequest(string? Visibility);
public sealed record PagePublishRequest(bool Published);

/// <summary>Whether a project's curated page is published. Its own small shape rather than a field added to
/// the GitLab-compatible project payload, because GitLab has no such concept and a payload somebody else
/// owns should not grow a field only this server understands.</summary>
public sealed record PageStateResponse(long ProjectId, bool Published);

/// <summary>
/// A published project page, as an anonymous reader sees it. Carries only what the page config named: there
/// is no field here that could describe the repository behind it.
/// </summary>
public sealed record ProjectPageResponse(
    string Title,
    string Description,
    /// <summary>The project's shareable address, which names no account.</summary>
    string Slug,
    /// <summary>The rendered readme the config named, when it named one that exists.</summary>
    ReadmeResponse? Readme,
    /// <summary>The files this page offers, already filtered to what the allowlist admits.</summary>
    IReadOnlyList<string> Files,
    /// <summary>Where this project's static site is served, or null when it publishes none.</summary>
    [property: JsonPropertyName("site_url")] string? SiteUrl);

/// <summary>One card in the public browser. Deliberately not the GitLab project shape: it carries only what
/// a stranger is shown, so there is no field here that could accidentally describe a private project.</summary>
public sealed record BrowseEntryResponse(
    long Id,
    string Name,
    string Path,
    string Namespace,
    string Description,
    string? AvatarUrl,
    string WebUrl,
    DateTimeOffset CreatedAt,
    string Visibility,
    /// <summary>Whether the source itself is public. False means this project is listed because it publishes
    /// a page, and its repository is not readable.</summary>
    bool SourcePublic,
    /// <summary>The project's instance-wide slug, which addresses its page and names no account.</summary>
    [property: JsonPropertyName("global_slug")] string GlobalSlug = "");

public sealed record BrowseResponse(
    IReadOnlyList<BrowseEntryResponse> Projects,
    long Total,
    int Page,
    int PerPage);

/// <summary>The administrator-facing state of the forge's own Model Context Protocol endpoint. Reported only
/// when the operator started the server with the endpoint available; otherwise the route is absent.</summary>
public sealed record McpSettingsResponse(
    /// <summary>Whether agents are currently served at all. An administrator switch, under the operator's.</summary>
    bool Enabled,
    /// <summary>Whether tools that change something are permitted for any caller.</summary>
    bool AllowWrites,
    /// <summary>Whether callers who present no credential are served, as strangers.</summary>
    bool AllowAnonymous,
    /// <summary>Where a client connects, as an absolute URL on the public origin.</summary>
    string Endpoint);

public sealed record McpSettingsRequest(
    bool Enabled,
    bool AllowWrites,
    bool AllowAnonymous);

/// <summary>
/// Confirms a project deletion by naming the project. The browser requires it; the API does not, because
/// there the caller is a program that already had to choose the path it sent.
/// </summary>
public sealed record ProjectDeleteRequest(string? Path);

/// <summary>One recorded act, as an administrator reads it. No field here can hold a credential.</summary>
public sealed record AuditEntryResponse(
    long Id,
    [property: JsonPropertyName("actor_user_id")] long? ActorUserId,
    [property: JsonPropertyName("actor_username")] string? ActorUsername,
    string Action,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("target_id")] string? TargetId,
    string Detail,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record AuditPageResponse(
    IReadOnlyList<AuditEntryResponse> Entries,
    long Total,
    int Page,
    int PerPage);

/// <summary>
/// An issue, in GitLab's shape. <c>iid</c> is the number people use and <c>id</c> is the row; GitLab
/// carries both and so does this, because a client that addresses issues by the wrong one of the two
/// silently works until two projects have issues.
/// </summary>
public sealed record IssueResponse(
    long Id,
    long Iid,
    [property: JsonPropertyName("project_id")] long ProjectId,
    string Title,
    string Description,
    string State,
    UserResponse Author,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("closed_at")] DateTimeOffset? ClosedAt,
    [property: JsonPropertyName("web_url")] string WebUrl,
    /// <summary>How many comments it has. Graft's own field, so a listing can show it without a second
    /// request for every row.</summary>
    [property: JsonPropertyName("user_notes_count")] long UserNotesCount = 0);

/// <summary>
/// A merge proposal. GitLab calls this a merge request and so does the API surface; the Graft code calls
/// it a proposal, because that is what it is until somebody merges it.
/// </summary>
public sealed record MergeRequestResponse(
    long Id,
    long Iid,
    [property: JsonPropertyName("project_id")] long ProjectId,
    string Title,
    string Description,
    string State,
    [property: JsonPropertyName("source_branch")] string SourceBranch,
    [property: JsonPropertyName("target_branch")] string TargetBranch,
    UserResponse Author,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("closed_at")] DateTimeOffset? ClosedAt,
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha,
    [property: JsonPropertyName("merged_by_username")] string? MergedByUsername,
    /// <summary>GitLab's own vocabulary: <c>can_be_merged</c>, <c>cannot_be_merged</c>, or
    /// <c>unchecked</c> when a branch the proposal names no longer resolves.</summary>
    [property: JsonPropertyName("merge_status")] string MergeStatus,
    /// <summary>The source branch tip, or null when the branch is gone.</summary>
    string? Sha,
    [property: JsonPropertyName("web_url")] string WebUrl,
    /// <summary>
    /// How this would be merged, if it can be. Graft's own field, because Graft's answer is narrower than
    /// GitLab's and saying so is more useful than a bare <c>cannot_be_merged</c>: <c>already_merged</c>,
    /// <c>fast_forward</c>, <c>three_way</c>, <c>conflict</c>, or <c>unresolved</c>.
    /// </summary>
    [property: JsonPropertyName("merge_method")] string MergeMethod = "unresolved",
    /// <summary>The paths both branches changed, which is the whole reason a merge is refused when it is.</summary>
    IReadOnlyList<string>? Conflicts = null,
    [property: JsonPropertyName("user_notes_count")] long UserNotesCount = 0);

public sealed record IssuePageResponse(IReadOnlyList<IssueResponse> Issues, long Total, int Page, int PerPage);
public sealed record MergeRequestPageResponse(IReadOnlyList<MergeRequestResponse> MergeRequests, long Total, int Page, int PerPage);

/// <summary>A comment. GitLab calls these notes and the API path says so.</summary>
public sealed record NoteResponse(
    long Id,
    string Body,
    UserResponse Author,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record IssueCreateRequest(string? Title, string? Description);

/// <summary>
/// Edits an issue or a proposal. <c>state_event</c> is GitLab's spelling and takes <c>close</c> or
/// <c>reopen</c>, so a state is changed by naming the act rather than by asserting the result.
/// </summary>
public sealed record IssueUpdateRequest(
    string? Title,
    string? Description,
    [property: JsonPropertyName("state_event")] string? StateEvent);

public sealed record NoteCreateRequest(string? Body);

public sealed record MergeRequestCreateRequest(
    [property: JsonPropertyName("source_branch")] string? SourceBranch,
    [property: JsonPropertyName("target_branch")] string? TargetBranch,
    string? Title,
    string? Description);

/// <summary>What a merge produced, or why it could not happen.</summary>
public sealed record MergeResultResponse(
    MergeRequestResponse MergeRequest,
    /// <summary><c>fast_forward</c> or <c>three_way</c>: how the target branch actually moved.</summary>
    string Method,
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha);

/// <summary>A proposal and what it would change, measured from the point the two branches last agreed.</summary>
public sealed record MergeRequestChangesResponse(
    [property: JsonPropertyName("merge_request")] MergeRequestResponse MergeRequest,
    [property: JsonPropertyName("base_sha")] string? BaseSha,
    [property: JsonPropertyName("source_sha")] string? SourceSha,
    [property: JsonPropertyName("target_sha")] string? TargetSha,
    IReadOnlyList<DiffFileResponse> Files,
    bool Truncated,
    int Added,
    int Removed);

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
[JsonSerializable(typeof(TokenRevealRequest))]
[JsonSerializable(typeof(TokenSecretRequest))]
[JsonSerializable(typeof(TokenRevealResponse))]
[JsonSerializable(typeof(TokenResponse[]))]
[JsonSerializable(typeof(ProjectCreateRequest))]
[JsonSerializable(typeof(MembershipSetRequest))]
[JsonSerializable(typeof(ProjectDetailsRequest))]
[JsonSerializable(typeof(OwnershipTransferRequest))]
[JsonSerializable(typeof(DefaultBranchRequest))]
[JsonSerializable(typeof(ProjectMemberResponse))]
[JsonSerializable(typeof(ProjectMemberResponse[]))]
[JsonSerializable(typeof(RepositoryPageResponse))]
[JsonSerializable(typeof(ReadmeResponse))]
[JsonSerializable(typeof(CommitDiffResponse))]
[JsonSerializable(typeof(UploadIngestRequest))]
[JsonSerializable(typeof(BundleInventoryResponse))]
[JsonSerializable(typeof(RepositoryCommitRequest))]
[JsonSerializable(typeof(RepositoryFileWriteRequest))]
[JsonSerializable(typeof(RepositoryCommitResponse))]
[JsonSerializable(typeof(UploadStatusResponse))]
[JsonSerializable(typeof(VisibilityRequest))]
[JsonSerializable(typeof(PagePublishRequest))]
[JsonSerializable(typeof(PageStateResponse))]
[JsonSerializable(typeof(ProjectPageResponse))]
[JsonSerializable(typeof(BrowseResponse))]
[JsonSerializable(typeof(McpSettingsResponse))]
[JsonSerializable(typeof(McpSettingsRequest))]
[JsonSerializable(typeof(ProjectDeleteRequest))]
[JsonSerializable(typeof(AuditPageResponse))]
[JsonSerializable(typeof(IssueResponse))]
[JsonSerializable(typeof(IssueResponse[]))]
[JsonSerializable(typeof(IssuePageResponse))]
[JsonSerializable(typeof(MergeRequestResponse))]
[JsonSerializable(typeof(MergeRequestResponse[]))]
[JsonSerializable(typeof(MergeRequestPageResponse))]
[JsonSerializable(typeof(MergeRequestChangesResponse))]
[JsonSerializable(typeof(MergeResultResponse))]
[JsonSerializable(typeof(NoteResponse))]
[JsonSerializable(typeof(NoteResponse[]))]
[JsonSerializable(typeof(IssueCreateRequest))]
[JsonSerializable(typeof(IssueUpdateRequest))]
[JsonSerializable(typeof(NoteCreateRequest))]
[JsonSerializable(typeof(MergeRequestCreateRequest))]
public partial class ApiJson : JsonSerializerContext;
