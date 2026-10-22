namespace Graft.Core;

/// <summary>
/// The things a project can ask to be told about.
///
/// <para>
/// A closed set rather than an open string, so a hook that names an event nobody will ever send is refused
/// when it is created rather than silently never firing.
/// </para>
/// </summary>
[Flags]
public enum WebhookEvent
{
    None = 0,
    /// <summary>A branch moved, by push or by merge. Carries the branch, the old and new commits, and who.</summary>
    Push = 1,
    /// <summary>An issue was opened, closed or reopened.</summary>
    Issue = 2,
    /// <summary>A merge proposal was opened, closed, reopened or merged.</summary>
    MergeRequest = 4,
    /// <summary>Somebody commented on either.</summary>
    Comment = 8
}

/// <summary>
/// A registered hook. The secret is never carried here: it exists only to sign a delivery, it is written
/// once when the hook is created, and nothing reads it back out of the store except the sender.
/// </summary>
public sealed record Webhook(
    long Id,
    long ProjectId,
    string Url,
    WebhookEvent Events,
    bool Active,
    string CreatedByUsername,
    DateTimeOffset CreatedAtUtc,
    /// <summary>The HTTP status of the last attempt, or null when nothing has been attempted.</summary>
    int? LastStatus,
    /// <summary>Why the last attempt failed, when it failed before producing a status at all.</summary>
    string? LastError,
    DateTimeOffset? LastDeliveredAtUtc);

/// <summary>A hook and the secret to sign with. Read only by the sender, never by a route.</summary>
public sealed record WebhookTarget(long Id, string Url, string Secret);

public static class WebhookEvents
{
    /// <summary>The names used on the wire and in the API, in a fixed order so a stored set round-trips.</summary>
    public static readonly (string Name, WebhookEvent Value)[] All =
    [
        ("push", WebhookEvent.Push),
        ("issue", WebhookEvent.Issue),
        ("merge_request", WebhookEvent.MergeRequest),
        ("comment", WebhookEvent.Comment)
    ];

    public static string Format(WebhookEvent events) =>
        string.Join(",", All.Where(entry => events.HasFlag(entry.Value)).Select(entry => entry.Name));

    /// <summary>Reads a stored or submitted set. An unknown name fails rather than being dropped, so a
    /// typo in a hook's configuration is visible at the moment it is made.</summary>
    public static bool TryParse(string? value, out WebhookEvent events)
    {
        events = WebhookEvent.None;
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (string Name, WebhookEvent Value)? match = All.FirstOrDefault(entry =>
                string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not { Name.Length: > 0 }) return false;
            events |= match.Value.Value;
        }
        return events != WebhookEvent.None;
    }

    public static string Name(WebhookEvent single) =>
        All.FirstOrDefault(entry => entry.Value == single).Name ?? "unknown";
}
