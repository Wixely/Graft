using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// The inbox: what happened that concerns a person, written in the same transaction as the thing that
/// happened. Being assigned, being asked to review, and anything done to an issue or proposal the person is
/// part of: commented on, approved, merged, closed, reopened. The actor is never told about their own act.
///
/// <para>
/// A row is the notification; email is only ever a copy of a row, sent by whoever hosts this store through
/// <see cref="NotificationCreated"/>, after the transaction commits and only when the person asked for it.
/// A server that sends nothing therefore has the same inbox as one that does.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>Raised once per notification written, after its transaction committed. The host decides
    /// whether a message goes out; the store has already decided whether the row exists.</summary>
    public Func<NotificationDelivery, Task>? NotificationCreated { get; set; }

    /// <summary>Most notifications kept per person; beyond it the oldest read ones go.</summary>
    private const int MaximumNotificationsPerUser = 500;

    /// <summary>
    /// Whether this server serves anything to somebody who is not a member. A mention reaches a non-member only when it
    /// does: the store's own rules would let a public project's stranger read the issue, but on a server whose operator
    /// turned public browsing off, the inbox entry would name something the link then refuses.
    /// </summary>
    public bool PublicBrowsing { get; set; }

    /// <summary>The events a participant hears about because they took part. Muting silences these; being assigned,
    /// asked to review or mentioned is addressed to the person by name and always arrives (ADR 0050).</summary>
    private static readonly HashSet<string> ParticipationActions = new(StringComparer.Ordinal) { "commented", "approved", "merged", "closed", "reopened" };

    /// <summary>Most items one digest lists; beyond it the message says how many more there were.</summary>
    private const int MaximumDigestItems = 100;

    public async Task<NotificationPage> ListNotificationsAsync(AuthenticatedIdentity actor, bool unreadOnly, int page, int perPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (page < 1 || perPage is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        long unread = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Notifications WHERE UserId = @UserId AND ReadAtUtc IS NULL;",
            new UserParameters(liveActor.UserId));
        long total = unreadOnly ? unread : await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Notifications WHERE UserId = @UserId;",
            new UserParameters(liveActor.UserId));
        IEnumerable<NotificationRow> rows = await db.QueryAsync<NotificationRow>(NotificationSelect + "\n" + """
            WHERE n.UserId = @UserId AND (@UnreadOnly = 0 OR n.ReadAtUtc IS NULL)
            ORDER BY n.Id DESC
            LIMIT @Limit OFFSET @Offset;
            """, new NotificationListParameters(liveActor.UserId, unreadOnly ? 1 : 0, perPage, (page - 1) * perPage));
        return new(rows.Select(ToNotification).ToArray(), unread, total);
    }

    public async Task<long> CountUnreadNotificationsAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Notifications WHERE UserId = @UserId AND ReadAtUtc IS NULL;",
            new UserParameters(userId));
    }

    /// <summary>Marks one of the caller's notifications read. False when there is no such notification of theirs.</summary>
    public async Task<bool> MarkNotificationReadAsync(AuthenticatedIdentity actor, long notificationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        int changed = await db.ExecuteAsync("""
            UPDATE Notifications SET ReadAtUtc = COALESCE(ReadAtUtc, @NowUtc) WHERE Id = @Id AND UserId = @UserId;
            """, new MarkReadParameters(notificationId, liveActor.UserId, Now()));
        return changed == 1;
    }

    public async Task MarkAllNotificationsReadAsync(AuthenticatedIdentity actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        await db.ExecuteAsync("UPDATE Notifications SET ReadAtUtc = @NowUtc WHERE UserId = @UserId AND ReadAtUtc IS NULL;",
            new MarkAllReadParameters(liveActor.UserId, Now()));
    }

    /// <summary>
    /// Whether the caller wants email, and whether as one message per notification or as a digest. Off by default; the
    /// inbox is always there. Null <paramref name="digest"/> leaves that choice alone. Turning the digest on starts its
    /// clock now, so the first one carries what arrives from here on rather than everything the inbox ever held.
    /// </summary>
    public async Task SetNotifyByEmailAsync(AuthenticatedIdentity actor, bool enabled, bool? digest = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        await db.ExecuteAsync("""
            UPDATE Users
            SET LastDigestAtUtc = CASE WHEN @Digest = 1 AND (EmailDigest = 0 OR NotifyByEmail = 0) THEN @NowUtc ELSE LastDigestAtUtc END,
                NotifyByEmail = @Enabled,
                EmailDigest = CASE WHEN @Digest = -1 THEN EmailDigest ELSE @Digest END
            WHERE Id = @Id;
            """, new EmailPreferenceParameters(liveActor.UserId, enabled ? 1 : 0, digest is null ? -1 : digest.Value ? 1 : 0, Now()));
    }

    public async Task<bool> GetEmailDigestAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteScalarAsync<long>("SELECT COALESCE((SELECT EmailDigest FROM Users WHERE Id = @UserId), 0);",
            new UserParameters(userId)) != 0;
    }

    /// <summary>
    /// The digests that are due, each marked sent in the same transaction so two workers, or a restart, never send one
    /// twice. Due means the person asked for email as a digest, has an address, and the last one went at least
    /// <paramref name="period"/> ago. Only unread notifications are listed: one already read in the inbox needs no
    /// message. A person with nothing new is skipped, and their clock still moves.
    /// </summary>
    public async Task<IReadOnlyList<NotificationDigest>> TakeDueDigestsAsync(TimeSpan period, CancellationToken cancellationToken = default)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        DateTimeOffset nowInstant = DateTimeOffset.UtcNow;
        string now = nowInstant.ToString("O", CultureInfo.InvariantCulture);
        string cutoff = (nowInstant - period).ToString("O", CultureInfo.InvariantCulture);
        IEnumerable<DigestRecipientRow> due = await db.QueryAsync<DigestRecipientRow>("""
            SELECT Id, Username, Email, LastDigestAtUtc FROM Users
            WHERE NotifyByEmail = 1 AND EmailDigest = 1 AND Disabled = 0 AND Email IS NOT NULL
              AND (LastDigestAtUtc IS NULL OR LastDigestAtUtc <= @Cutoff);
            """, new DigestDueParameters(cutoff), transaction);
        List<NotificationDigest> digests = [];
        foreach (DigestRecipientRow recipient in due.ToArray())
        {
            string since = recipient.LastDigestAtUtc ?? cutoff;
            long total = await db.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM Notifications WHERE UserId = @UserId AND ReadAtUtc IS NULL AND CreatedAtUtc > @Since;",
                new DigestItemsParameters(recipient.Id, since, MaximumDigestItems), transaction);
            if (total > 0)
            {
                IEnumerable<NotificationRow> rows = await db.QueryAsync<NotificationRow>(NotificationSelect + "\n" + """
                    WHERE n.UserId = @UserId AND n.ReadAtUtc IS NULL AND n.CreatedAtUtc > @Since
                    ORDER BY n.Id
                    LIMIT @Limit;
                    """, new DigestItemsParameters(recipient.Id, since, MaximumDigestItems), transaction);
                digests.Add(new(recipient.Id, recipient.Username, recipient.Email, rows.Select(ToNotification).ToArray(), total));
            }
            await db.ExecuteAsync("UPDATE Users SET LastDigestAtUtc = @NowUtc WHERE Id = @UserId;", new DigestStampParameters(recipient.Id, now), transaction);
        }
        await transaction.CommitAsync(cancellationToken);
        return digests;
    }

    /// <summary>Whether the caller muted this project, and this issue or proposal in it. Needs what reading it needs.</summary>
    public async Task<MuteState> GetMuteStateAsync(AuthenticatedIdentity actor, long projectId, CommentSubject? subject, long? iid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        long subjectId = await RequireMutableAsync(db, null, live, projectId, subject, iid, cancellationToken);
        bool project = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM NotificationMutes WHERE UserId = @UserId AND ProjectId = @ProjectId AND SubjectType = '' AND SubjectId = 0;",
            new MuteParameters(live.UserId, projectId, "", 0, ""), null) > 0;
        bool thread = subject is not null && await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM NotificationMutes WHERE UserId = @UserId AND ProjectId = @ProjectId AND SubjectType = @SubjectType AND SubjectId = @SubjectId;",
            new MuteParameters(live.UserId, projectId, SubjectName(subject.Value), subjectId, ""), null) > 0;
        return new(project, thread);
    }

    /// <summary>
    /// Mutes or unmutes a project, or with <paramref name="subject"/> one issue or proposal in it, for the caller.
    /// Returns whether anything changed, which GitLab's subscribe routes report as 304 when it did not.
    /// </summary>
    public async Task<bool> SetMutedAsync(AuthenticatedIdentity actor, long projectId, CommentSubject? subject, long? iid, bool muted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        long subjectId = await RequireMutableAsync(db, transaction, live, projectId, subject, iid, cancellationToken);
        MuteParameters key = new(live.UserId, projectId, subject is null ? "" : SubjectName(subject.Value), subject is null ? 0 : subjectId, Now());
        int changed = muted
            ? await db.ExecuteAsync("""
                INSERT INTO NotificationMutes (UserId, ProjectId, SubjectType, SubjectId, CreatedAtUtc)
                VALUES (@UserId, @ProjectId, @SubjectType, @SubjectId, @CreatedAtUtc)
                ON CONFLICT DO NOTHING;
                """, key, transaction)
            : await db.ExecuteAsync(
                "DELETE FROM NotificationMutes WHERE UserId = @UserId AND ProjectId = @ProjectId AND SubjectType = @SubjectType AND SubjectId = @SubjectId;",
                key, transaction);
        await transaction.CommitAsync(cancellationToken);
        return changed > 0;
    }

    /// <summary>Everything the caller muted, newest first, for the inbox page to list and undo.</summary>
    public async Task<IReadOnlyList<NotificationMute>> ListMutesAsync(AuthenticatedIdentity actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        IEnumerable<MuteRow> rows = await db.QueryAsync<MuteRow>("""
            SELECT m.ProjectId, o.Username AS Namespace, p.Slug, m.SubjectType,
                   COALESCE(i.Iid, r.Iid) AS Iid, COALESCE(i.Title, r.Title) AS Title, m.CreatedAtUtc
            FROM NotificationMutes m
            JOIN Projects p ON p.Id = m.ProjectId AND p.State = 'ready'
            JOIN Users o ON o.Id = p.OwnerUserId
            LEFT JOIN Issues i ON m.SubjectType = 'issue' AND i.Id = m.SubjectId
            LEFT JOIN MergeRequests r ON m.SubjectType = 'merge_request' AND r.Id = m.SubjectId
            WHERE m.UserId = @UserId AND (m.SubjectType = '' OR COALESCE(i.Id, r.Id) IS NOT NULL)
            ORDER BY m.CreatedAtUtc DESC;
            """, new UserParameters(live.UserId));
        return [.. rows.Select(row => new NotificationMute(row.ProjectId, row.Namespace + "/" + row.Slug,
            row.SubjectType switch { "issue" => CommentSubject.Issue, "merge_request" => CommentSubject.MergeRequest, _ => (CommentSubject?)null },
            row.Iid, row.Title, Instant(row.CreatedAtUtc)))];
    }

    /// <summary>A person may mute what they may read, and nothing else, so a mute cannot be used to learn whether an
    /// issue exists. Returns the issue or proposal's row id, or zero for the project itself.</summary>
    private static async Task<long> RequireMutableAsync(SqliteConnection db, SqliteTransaction? transaction, AuthenticatedIdentity identity,
        long projectId, CommentSubject? subject, long? iid, CancellationToken cancellationToken)
    {
        PermissionDecision reader = await RequireReaderAsync(db, transaction, identity, projectId, cancellationToken);
        if (subject is null) return 0;
        if (iid is null) throw new ArgumentException("A thread is named by its number.", nameof(iid));
        long subjectId = await ResolveSubjectAsync(db, transaction, projectId, subject.Value, iid.Value);
        if (subject == CommentSubject.Issue)
        {
            IssueRow? issue = await db.QuerySingleOrDefaultAsync<IssueRow>(IssueSelect + "WHERE i.Id = @Id;", new IdParameters(subjectId), transaction);
            if (issue is null || !MaySeeIssue(issue, reader, identity)) throw new CollaborationNotFoundException("Issue is unavailable.");
        }
        return subjectId;
    }

    /// <summary>
    /// Tells each person a body mentions, once, with <c>mentioned</c>, and returns who that was so the caller can leave
    /// them out of a broader notice about the same act. A name that is nobody, a disabled account, the actor, and anybody
    /// who could not read the issue or proposal are passed over in silence: a mention must never tell a stranger a title.
    /// </summary>
    private static async Task<(List<NotificationDelivery> Deliveries, HashSet<long> Mentioned)> NotifyMentionsAsync(SqliteConnection db,
        SqliteTransaction transaction, long actorUserId, long projectId, CommentSubject subject, long subjectId, IReadOnlyList<string> names,
        bool reachNonMembers, string now, CancellationToken cancellationToken)
    {
        HashSet<long> mentioned = [];
        if (names.Count == 0) return ([], mentioned);
        IEnumerable<IdentityRow> people = await db.QueryAsync<IdentityRow>("""
            SELECT Id, Username, IsAdministrator, Disabled FROM Users
            WHERE Disabled = 0 AND Id <> @UserId AND instr(@Names, ',' || NormalizedUsername || ',') > 0;
            """, new MentionLookupParameters(actorUserId, "," + string.Join(',', names) + ","), transaction);
        ConfidentialRow? confidential = subject == CommentSubject.Issue
            ? await db.QuerySingleOrDefaultAsync<ConfidentialRow>("SELECT Confidential, AuthorUserId FROM Issues WHERE Id = @Id;", new IdParameters(subjectId), transaction)
            : null;
        foreach (IdentityRow person in people.ToArray())
        {
            AuthenticatedIdentity identity = new(person.Id, person.Username, person.IsAdministrator != 0, IdentityKind.BrowserSession);
            PermissionDecision reader = await DecideAsync(db, transaction, identity, projectId, ProjectAction.ReadCollaboration, cancellationToken);
            if (!reader.Allowed || (reader.Grant != AccessGrant.Member && !reachNonMembers)) continue;
            if (confidential is { Confidential: not 0 } && reader.Grant != AccessGrant.Member && confidential.AuthorUserId != person.Id) continue;
            mentioned.Add(person.Id);
        }
        return (await NotifyAsync(db, transaction, actorUserId, projectId, subject, subjectId, "mentioned", mentioned, now), mentioned);
    }

    public async Task<bool> GetNotifyByEmailAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteScalarAsync<long>("SELECT COALESCE((SELECT NotifyByEmail FROM Users WHERE Id = @UserId), 0);",
            new UserParameters(userId)) != 0;
    }

    /// <summary>
    /// Everybody an issue or proposal concerns: its author, its assignees and reviewers, and whoever has
    /// commented on it. Disabled accounts are left out; so is <paramref name="except"/>, the actor.
    /// </summary>
    private static async Task<List<long>> ParticipantsAsync(SqliteConnection db, SqliteTransaction transaction, CommentSubject subject, long subjectId, long except)
    {
        string kind = SubjectName(subject);
        IEnumerable<long> ids = await db.QueryAsync<long>(subject == CommentSubject.Issue
            ? """
              SELECT u.Id FROM Users u WHERE u.Disabled = 0 AND u.Id <> @Except AND (
                  u.Id IN (SELECT AuthorUserId FROM Issues WHERE Id = @SubjectId)
                  OR u.Id IN (SELECT UserId FROM SubjectPeople WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId)
                  OR u.Id IN (SELECT AuthorUserId FROM Comments WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId));
              """
            : """
              SELECT u.Id FROM Users u WHERE u.Disabled = 0 AND u.Id <> @Except AND (
                  u.Id IN (SELECT AuthorUserId FROM MergeRequests WHERE Id = @SubjectId)
                  OR u.Id IN (SELECT UserId FROM SubjectPeople WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId)
                  OR u.Id IN (SELECT AuthorUserId FROM Comments WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId));
              """, new ParticipantParameters(kind, subjectId, except), transaction);
        return ids.ToList();
    }

    /// <summary>
    /// Writes one notification per recipient other than the actor, and returns what was written with each
    /// recipient's address and preference, for the caller to raise after the commit. Older read rows beyond
    /// the per-person bound are trimmed on the way.
    /// </summary>
    private static async Task<List<NotificationDelivery>> NotifyAsync(SqliteConnection db, SqliteTransaction transaction, long actorUserId,
        long projectId, CommentSubject subject, long subjectId, string action, IEnumerable<long> recipients, string now)
    {
        List<NotificationDelivery> deliveries = [];
        List<long> distinct = recipients.Where(id => id != actorUserId).Distinct().ToList();
        if (distinct.Count == 0) return deliveries;

        string kind = SubjectName(subject);
        if (ParticipationActions.Contains(action))
        {
            HashSet<long> muted = [.. await db.QueryAsync<long>("""
                SELECT UserId FROM NotificationMutes
                WHERE ProjectId = @ProjectId AND ((SubjectType = '' AND SubjectId = 0) OR (SubjectType = @SubjectType AND SubjectId = @SubjectId));
                """, new MutedParameters(projectId, kind, subjectId), transaction)];
            distinct.RemoveAll(muted.Contains);
            if (distinct.Count == 0) return deliveries;
        }
        NotificationSubjectRow? about = await db.QuerySingleOrDefaultAsync<NotificationSubjectRow>(subject == CommentSubject.Issue
            ? "SELECT i.Iid, i.Title, o.Username AS Namespace, p.Slug FROM Issues i JOIN Projects p ON p.Id = i.ProjectId JOIN Users o ON o.Id = p.OwnerUserId WHERE i.Id = @Id;"
            : "SELECT m.Iid, m.Title, o.Username AS Namespace, p.Slug FROM MergeRequests m JOIN Projects p ON p.Id = m.ProjectId JOIN Users o ON o.Id = p.OwnerUserId WHERE m.Id = @Id;",
            new IdParameters(subjectId), transaction);
        if (about is null) return deliveries;
        string actorUsername = (await db.QuerySingleOrDefaultAsync<RecipientRow>("SELECT Id, Username, Email, NotifyByEmail, EmailDigest FROM Users WHERE Id = @Id;", new IdParameters(actorUserId), transaction))?.Username ?? "";

        foreach (long userId in distinct)
        {
            RecipientRow? recipient = await db.QuerySingleOrDefaultAsync<RecipientRow>(
                "SELECT Id, Username, Email, NotifyByEmail, EmailDigest FROM Users WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId), transaction);
            if (recipient is null) continue;
            long id = await db.QuerySingleAsync<long>("""
                INSERT INTO Notifications (UserId, ProjectId, Action, SubjectType, SubjectIid, Title, ActorUserId, CreatedAtUtc, ReadAtUtc)
                VALUES (@UserId, @ProjectId, @Action, @SubjectType, @SubjectIid, @Title, @ActorUserId, @CreatedAtUtc, NULL)
                RETURNING Id;
                """, new NotificationInsertParameters(userId, projectId, action, kind, about.Iid, about.Title, actorUserId, now), transaction);
            await db.ExecuteAsync("""
                DELETE FROM Notifications WHERE UserId = @UserId AND ReadAtUtc IS NOT NULL AND Id NOT IN (
                    SELECT Id FROM Notifications WHERE UserId = @UserId ORDER BY Id DESC LIMIT @Limit);
                """, new TrimParameters(userId, MaximumNotificationsPerUser), transaction);
            deliveries.Add(new(userId, recipient.Username, recipient.Email, recipient.NotifyByEmail != 0,
                new(id, projectId, about.Namespace + "/" + about.Slug, action, subject, about.Iid, about.Title, actorUsername, Instant(now), null),
                recipient.EmailDigest != 0));
        }
        return deliveries;
    }

    /// <summary>Tells each person newly placed in a role: an assignee that they were assigned, a reviewer
    /// that a review was asked of them.</summary>
    private static async Task<List<NotificationDelivery>> NotifyAddedAsync(SqliteConnection db, SqliteTransaction transaction, long actorUserId,
        long projectId, CommentSubject subject, long subjectId, List<(string Role, long UserId)> added, string now)
    {
        List<NotificationDelivery> deliveries = [];
        foreach ((string role, long userId) in added)
            deliveries.AddRange(await NotifyAsync(db, transaction, actorUserId, projectId, subject, subjectId,
                role == "assignee" ? "assigned" : "review_requested", [userId], now));
        return deliveries;
    }

    /// <summary>Hands each written notification to the host, after the commit. A host that throws loses that
    /// message and nothing else; the row is already there.</summary>
    private async Task RaiseAsync(List<NotificationDelivery> deliveries)
    {
        Func<NotificationDelivery, Task>? created = NotificationCreated;
        if (created is null) return;
        foreach (NotificationDelivery delivery in deliveries)
        {
            try { await created(delivery); }
            catch (Exception) { }
        }
    }

    private const string NotificationSelect = """
        SELECT n.Id, n.ProjectId, o.Username AS Namespace, p.Slug, n.Action, n.SubjectType, n.SubjectIid, n.Title,
               COALESCE(a.Username, '') AS ActorUsername, n.CreatedAtUtc, n.ReadAtUtc
        FROM Notifications n
        JOIN Projects p ON p.Id = n.ProjectId
        JOIN Users o ON o.Id = p.OwnerUserId
        LEFT JOIN Users a ON a.Id = n.ActorUserId
        """;

    private static Notification ToNotification(NotificationRow row) => new(row.Id, row.ProjectId, row.Namespace + "/" + row.Slug, row.Action,
        row.SubjectType == "issue" ? CommentSubject.Issue : CommentSubject.MergeRequest, row.SubjectIid, row.Title, row.ActorUsername,
        Instant(row.CreatedAtUtc), row.ReadAtUtc is null ? null : Instant(row.ReadAtUtc));
}

internal sealed record NotificationRow(long Id, long ProjectId, string Namespace, string Slug, string Action, string SubjectType, long SubjectIid,
    string Title, string ActorUsername, string CreatedAtUtc, string? ReadAtUtc);
internal sealed record NotificationListParameters(long UserId, int UnreadOnly, int Limit, int Offset);
internal sealed record MarkReadParameters(long Id, long UserId, string NowUtc);
internal sealed record MarkAllReadParameters(long UserId, string NowUtc);
internal sealed record FlagParameters(long Id, int Enabled);
internal sealed record ParticipantParameters(string SubjectType, long SubjectId, long Except);
internal sealed record NotificationSubjectRow(long Iid, string Title, string Namespace, string Slug);
internal sealed record RecipientRow(long Id, string Username, string? Email, long NotifyByEmail, long EmailDigest);
internal sealed record EmailPreferenceParameters(long Id, int Enabled, int Digest, string NowUtc);
internal sealed record DigestDueParameters(string Cutoff);
internal sealed record DigestRecipientRow(long Id, string Username, string Email, string? LastDigestAtUtc);
internal sealed record DigestItemsParameters(long UserId, string Since, int Limit);
internal sealed record DigestStampParameters(long UserId, string NowUtc);
internal sealed record MuteParameters(long UserId, long ProjectId, string SubjectType, long SubjectId, string CreatedAtUtc);
internal sealed record MutedParameters(long ProjectId, string SubjectType, long SubjectId);
internal sealed record MuteRow(long ProjectId, string Namespace, string Slug, string SubjectType, long? Iid, string? Title, string CreatedAtUtc);
internal sealed record MentionLookupParameters(long UserId, string Names);
internal sealed record ConfidentialRow(long Confidential, long AuthorUserId);
internal sealed record CommentEditedRow(string Body, string SubjectType, long SubjectId);
internal sealed record NotificationInsertParameters(long UserId, long ProjectId, string Action, string SubjectType, long SubjectIid, string Title,
    long ActorUserId, string CreatedAtUtc);
internal sealed record TrimParameters(long UserId, int Limit);
