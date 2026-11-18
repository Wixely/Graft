using Graft.Storage;

namespace Graft.Server;

/// <summary>
/// What a push is held to for files locked in Git LFS (ADR 0057): a branch whose new commits change a path somebody
/// else has locked is refused, naming the path and who holds it. git-lfs checks the same before it pushes; this is the
/// forge holding a client to it that skipped the check or never had git-lfs at all. Shared by every way a push arrives.
/// </summary>
internal static class LfsLockRule
{
    /// <summary>The rule for one pusher, or null when nobody else holds a lock, so a push to a project without locks walks nothing.</summary>
    public static async Task<Func<IReadOnlySet<string>, string?>?> ForAsync(GraftStore store, long projectId, long? pusherUserId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LfsLockInfo> locks = await store.AllLfsLocksAsync(projectId, cancellationToken);
        Dictionary<string, string> theirs = new(StringComparer.Ordinal);
        foreach (LfsLockInfo held in locks)
            if (held.OwnerUserId != pusherUserId) theirs[held.Path] = held.OwnerUsername;
        if (theirs.Count == 0) return null;
        return paths =>
        {
            List<string> blocked = [.. paths.Where(theirs.ContainsKey).Order(StringComparer.Ordinal)];
            if (blocked.Count == 0) return null;
            string first = "'" + blocked[0] + "' is locked by " + theirs[blocked[0]];
            return blocked.Count == 1
                ? first + "; ask them to unlock it (git lfs unlock)"
                : first + " and " + (blocked.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more locked path" +
                  (blocked.Count == 2 ? "" : "s") + "; see git lfs locks";
        };
    }
}
