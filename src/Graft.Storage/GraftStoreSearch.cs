using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>An issue found across projects, with enough of its project to link to it.</summary>
public sealed record IssueHit(Issue Issue, string Namespace, string Slug);
public sealed record ProposalHit(MergeProposal Proposal, string Namespace, string Slug);

/// <summary>
/// Searching across projects.
///
/// <para>
/// Every query here carries the same visibility clause: a project is searched when the caller is an
/// administrator, its owner or a member, or when <paramref name="includePublic"/> says the server
/// publishes to strangers and the project is public or unlisted. That clause is the whole of the access
/// control for a search, so it is written once and the per-project rules are not re-derived here.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    private const string VisibleProjectClause = " " + """
        p.State = 'ready' AND (
            @IsAdministrator = 1 OR p.OwnerUserId = @UserId OR
            EXISTS (SELECT 1 FROM ProjectMemberships m WHERE m.ProjectId = p.Id AND m.UserId = @UserId) OR
            (@IncludePublic = 1 AND p.Visibility IN ('public', 'unlisted')))
        """;

    public async Task<IReadOnlyList<ProjectDetails>> SearchProjectsAsync(
        AuthenticatedIdentity? identity,
        string search,
        bool includePublic,
        int take,
        CancellationToken cancellationToken = default)
    {
        string pattern = Pattern(search);
        if (pattern is null || take is < 1 or > 100) return [];
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity? live = identity is null ? null : await FindLiveIdentityAsync(db, null, identity, cancellationToken);
        IEnumerable<ProjectDetailsRow> rows = await db.QueryAsync<ProjectDetailsRow>("""
            SELECT p.Id, p.OwnerUserId, COALESCE(g.FullPath, u.Username) AS Namespace, p.GroupId, p.Slug, p.Name, p.Description, p.StorageId, p.CreatedAtUtc,
                   p.DefaultBranch, p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            LEFT JOIN Groups g ON g.Id = p.GroupId
            WHERE
            """ + VisibleProjectClause + """

              AND (lower(p.Name) LIKE @Pattern OR lower(p.Slug) LIKE @Pattern OR lower(p.Description) LIKE @Pattern)
            ORDER BY p.Id DESC
            LIMIT @Take;
            """, new SearchParameters(live?.UserId ?? 0, live?.IsAdministrator == true ? 1 : 0, includePublic ? 1 : 0, pattern, take));
        return [.. rows.Select(ToDetails)];
    }

    public async Task<IReadOnlyList<IssueHit>> SearchIssuesAsync(
        AuthenticatedIdentity? identity,
        string search,
        bool includePublic,
        int take,
        CancellationToken cancellationToken = default)
    {
        string pattern = Pattern(search);
        if (pattern is null || take is < 1 or > 100) return [];
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity? live = identity is null ? null : await FindLiveIdentityAsync(db, null, identity, cancellationToken);
        // A confidential issue is found by members and its author, which is the listing's rule restated for
        // a caller who may hold only the public grant on this project.
        IEnumerable<IssueHitRow> rows = await db.QueryAsync<IssueHitRow>("""
            SELECT i.Id, i.ProjectId, i.Iid, i.AuthorUserId, a.Username AS AuthorUsername, i.Title, i.Description,
                   i.State, i.CreatedAtUtc, i.UpdatedAtUtc, i.ClosedAtUtc, i.DueDate, i.Confidential, i.TimeEstimateSeconds,
                   0 AS TimeSpentSeconds, i.MilestoneId, NULL AS MilestoneIid, NULL AS MilestoneTitle, 0 AS Comments,
                   COALESCE(g.FullPath, u.Username) AS Namespace, p.GroupId, p.Slug
            FROM Issues i
            JOIN Projects p ON p.Id = i.ProjectId
            JOIN Users u ON u.Id = p.OwnerUserId
            LEFT JOIN Groups g ON g.Id = p.GroupId
            JOIN Users a ON a.Id = i.AuthorUserId
            WHERE
            """ + VisibleProjectClause + """

              AND (lower(i.Title) LIKE @Pattern OR lower(i.Description) LIKE @Pattern)
              AND (i.Confidential = 0 OR i.AuthorUserId = @UserId OR @IsAdministrator = 1 OR p.OwnerUserId = @UserId OR
                   EXISTS (SELECT 1 FROM ProjectMemberships m WHERE m.ProjectId = p.Id AND m.UserId = @UserId))
            ORDER BY i.UpdatedAtUtc DESC
            LIMIT @Take;
            """, new SearchParameters(live?.UserId ?? 0, live?.IsAdministrator == true ? 1 : 0, includePublic ? 1 : 0, pattern, take));
        return [.. rows.Select(row => new IssueHit(ToIssue(row), row.Namespace, row.Slug))];
    }

    public async Task<IReadOnlyList<ProposalHit>> SearchMergeProposalsAsync(
        AuthenticatedIdentity? identity,
        string search,
        bool includePublic,
        int take,
        CancellationToken cancellationToken = default)
    {
        string pattern = Pattern(search);
        if (pattern is null || take is < 1 or > 100) return [];
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity? live = identity is null ? null : await FindLiveIdentityAsync(db, null, identity, cancellationToken);
        IEnumerable<ProposalHitRow> rows = await db.QueryAsync<ProposalHitRow>("""
            SELECT m.Id, m.ProjectId, m.Iid, m.AuthorUserId, a.Username AS AuthorUsername, m.Title, m.Description,
                   m.SourceBranch, m.TargetBranch, m.State, m.MergeCommitSha, NULL AS MergedByUsername,
                   m.SourceProjectId, NULL AS SourceSlug, NULL AS SourceNamespace,
                   m.CreatedAtUtc, m.UpdatedAtUtc, m.ClosedAtUtc, m.Draft, m.TimeEstimateSeconds, 0 AS TimeSpentSeconds,
                   m.MilestoneId, NULL AS MilestoneIid, NULL AS MilestoneTitle, 0 AS Comments, p.ApprovalsRequired,
                   COALESCE(g.FullPath, u.Username) AS Namespace, p.GroupId, p.Slug
            FROM MergeRequests m
            JOIN Projects p ON p.Id = m.ProjectId
            JOIN Users u ON u.Id = p.OwnerUserId
            LEFT JOIN Groups g ON g.Id = p.GroupId
            JOIN Users a ON a.Id = m.AuthorUserId
            WHERE
            """ + VisibleProjectClause + """

              AND (lower(m.Title) LIKE @Pattern OR lower(m.Description) LIKE @Pattern)
            ORDER BY m.UpdatedAtUtc DESC
            LIMIT @Take;
            """, new SearchParameters(live?.UserId ?? 0, live?.IsAdministrator == true ? 1 : 0, includePublic ? 1 : 0, pattern, take));
        return [.. rows.Select(row => new ProposalHit(ToProposal(row), row.Namespace, row.Slug))];
    }

    /// <summary>Wiki pages of one project whose title or content holds the query, without their content.</summary>
    public async Task<IReadOnlyList<WikiPage>> SearchWikiAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string search,
        int take,
        CancellationToken cancellationToken = default)
    {
        string pattern = Pattern(search);
        if (pattern is null || take is < 1 or > 100) return [];
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IEnumerable<WikiRow> rows = await db.QueryAsync<WikiRow>(WikiSelect.Replace("p.Content,", "'' AS Content,", StringComparison.Ordinal) + """
            WHERE p.ProjectId = @ProjectId AND (lower(p.Title) LIKE @Pattern OR lower(p.Content) LIKE @Pattern)
            ORDER BY lower(p.Title)
            LIMIT @Take;
            """, new WikiSearchParameters(projectId, pattern, take));
        return [.. rows.Select(ToWikiPage)];
    }

    /// <summary>A LIKE pattern for a case-insensitive substring, or null when there is nothing to look for.</summary>
    private static string Pattern(string search)
    {
        string trimmed = (search ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length is 0 or > 200) return null!;
        return "%" + trimmed.Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal) + "%";
    }
}

internal sealed record SearchParameters(long UserId, int IsAdministrator, int IncludePublic, string Pattern, int Take);
internal sealed record WikiSearchParameters(long ProjectId, string Pattern, int Take);

internal sealed class IssueHitRow : IssueRowBase
{
    public string Namespace { get; set; } = "";
    public string Slug { get; set; } = "";
}

internal sealed class ProposalHitRow : MergeRowBase
{
    public string Namespace { get; set; } = "";
    public string Slug { get; set; } = "";
}
