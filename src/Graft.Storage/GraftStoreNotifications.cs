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

    /// <summary>Whether the caller wants a message for each notification. Off by default; the inbox is always there.</summary>
    public async Task SetNotifyByEmailAsync(AuthenticatedIdentity actor, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        await db.ExecuteAsync("UPDATE Users SET NotifyByEmail = @Enabled WHERE Id = @Id;", new FlagParameters(liveActor.UserId, enabled ? 1 : 0));
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
        NotificationSubjectRow? about = await db.QuerySingleOrDefaultAsync<NotificationSubjectRow>(subject == CommentSubject.Issue
            ? "SELECT i.Iid, i.Title, o.Username AS Namespace, p.Slug FROM Issues i JOIN Projects p ON p.Id = i.ProjectId JOIN Users o ON o.Id = p.OwnerUserId WHERE i.Id = @Id;"
            : "SELECT m.Iid, m.Title, o.Username AS Namespace, p.Slug FROM MergeRequests m JOIN Projects p ON p.Id = m.ProjectId JOIN Users o ON o.Id = p.OwnerUserId WHERE m.Id = @Id;",
            new IdParameters(subjectId), transaction);
        if (about is null) return deliveries;
        string actorUsername = (await db.QuerySingleOrDefaultAsync<RecipientRow>("SELECT Id, Username, Email, NotifyByEmail FROM Users WHERE Id = @Id;", new IdParameters(actorUserId), transaction))?.Username ?? "";

        foreach (long userId in distinct)
        {
            RecipientRow? recipient = await db.QuerySingleOrDefaultAsync<RecipientRow>(
                "SELECT Id, Username, Email, NotifyByEmail FROM Users WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId), transaction);
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
                new(id, projectId, about.Namespace + "/" + about.Slug, action, subject, about.Iid, about.Title, actorUsername, Instant(now), null)));
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
internal sealed record RecipientRow(long Id, string Username, string? Email, long NotifyByEmail);
internal sealed record NotificationInsertParameters(long UserId, long ProjectId, string Action, string SubjectType, long SubjectIid, string Title,
    long ActorUserId, string CreatedAtUtc);
internal sealed record TrimParameters(long UserId, int Limit);
