using System.Globalization;

namespace Graft.Git;

/// <summary>What happened to one reference when a mirror was brought up to date.</summary>
public enum MirrorRefChange
{
    /// <summary>The upstream has a branch or tag this repository did not; it was added.</summary>
    Created,
    /// <summary>A branch moved forward upstream, and followed.</summary>
    Updated,
    /// <summary>A branch or tag upstream no longer contains what this repository had. It now follows the upstream,
    /// and what it pointed at before is kept under <see cref="RepositoryMirror.PreservedPrefix"/>.</summary>
    Rewritten,
    /// <summary>Gone upstream and kept here, untouched.</summary>
    Kept,
    /// <summary>Upstream points it at something this repository will not store, such as a branch that is not a commit.</summary>
    Skipped
}

public sealed record MirrorRefResult(string Name, MirrorRefChange Change, string? Before, string? After, string? Preserved = null, string? Reason = null);

public sealed record MirrorResult(IReadOnlyList<MirrorRefResult> References, int ObjectsReceived)
{
    /// <summary>Whether the repository changed: a reference moved or appeared.</summary>
    public bool Changed => References.Any(reference => reference.Change is MirrorRefChange.Created or MirrorRefChange.Updated or MirrorRefChange.Rewritten);
}

/// <summary>
/// Brings a repository up to date with another's branches and tags without ever losing anything it held.
///
/// <para>
/// A mirror exists so that a copy survives its upstream. So the upstream may add and advance, and is followed when it
/// rewrites, but cannot take anything away: a branch or tag deleted upstream stays here, and one rewritten upstream
/// keeps its previous tip under <c>refs/graft-preserved/&lt;time&gt;/</c>, a namespace only the server writes and any
/// client may fetch. A takedown that empties or rewrites the upstream leaves everything this repository had in place.
/// </para>
/// </summary>
public static class RepositoryMirror
{
    public const string PreservedPrefix = "refs/graft-preserved/";

    /// <summary>
    /// Reads <paramref name="pack"/> (null when no objects were needed) and points this repository's branches and tags
    /// where <paramref name="upstream"/> says, under the rules above, validating the batch the way a push is validated
    /// and committing it through the same durable path. The caller holds the repository's gate.
    /// </summary>
    public static MirrorResult Apply(Repository repository, Stream? pack, IReadOnlyDictionary<string, string> upstream,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(upstream);
        using TemporaryStorage? staging = repository.IsDurable && pack is not null ? new() : null;
        PackResult received = pack is null
            ? new PackResult(new(StringComparer.Ordinal), 0, 0)
            : Pack.Read(pack, repository.Objects, cancellationToken, staging?.DirectoryPath);
        Dictionary<string, GitObject> objects = new(repository.Objects, StringComparer.Ordinal);
        foreach ((string id, GitObject value) in received.Objects) objects[id] = value;
        Limits.Require(objects.Count <= Limits.ObjectCount && objects.Values.Sum(value => (long)value.Size) <= Limits.ExpandedBytes, "repository-budget");
        Graph.Walk(objects, received.Objects.Keys, cancellationToken);

        string stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        Dictionary<string, string> refs = new(repository.Refs, StringComparer.Ordinal);
        List<MirrorRefResult> results = [];

        string? Preserve(string name, string id)
        {
            string preserved = PreservedPrefix + stamp + "/" + name["refs/".Length..];
            if (!Limits.IsRef(preserved) || refs.ContainsKey(preserved)) return null;
            refs[preserved] = id;
            return preserved;
        }

        foreach ((string name, string id) in upstream.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Limits.IsClientRef(name) || !Limits.IsId(id)) { results.Add(new(name, MirrorRefChange.Skipped, null, id, Reason: "not a branch or tag name Graft stores")); continue; }
            string? current = refs.GetValueOrDefault(name);
            if (current == id) continue;
            bool branch = name.StartsWith("refs/heads/", StringComparison.Ordinal);
            if (!objects.TryGetValue(id, out GitObject? target)) { results.Add(new(name, MirrorRefChange.Skipped, current, id, Reason: "the upstream did not send it")); continue; }
            if (branch && target.Type != 1) { results.Add(new(name, MirrorRefChange.Skipped, current, id, Reason: "a branch must point at a commit")); continue; }
            Graph.Walk(objects, [id], cancellationToken);

            // A reference this repository kept after the upstream deleted it can stand in the way of a new one, as
            // "a" does of "a/b". The kept one moves aside into the preserved namespace; the upstream's is the live name.
            foreach (string conflict in refs.Keys.Where(other => Limits.IsClientRef(other) && !upstream.ContainsKey(other) &&
                (other.StartsWith(name + "/", StringComparison.Ordinal) || name.StartsWith(other + "/", StringComparison.Ordinal))).ToList())
            {
                string before = refs[conflict];
                refs.Remove(conflict);
                results.Add(new(conflict, MirrorRefChange.Rewritten, before, null, Preserve(conflict, before), "moved aside for " + name));
            }

            if (current is null) results.Add(new(name, MirrorRefChange.Created, null, id));
            else if (branch && objects.TryGetValue(current, out GitObject? previous) && previous.Type == 1 && Graph.IsAncestor(objects, current, id, cancellationToken))
                results.Add(new(name, MirrorRefChange.Updated, current, id));
            else results.Add(new(name, MirrorRefChange.Rewritten, current, id, Preserve(name, current)));
            refs[name] = id;
        }
        foreach (string name in repository.Refs.Keys.Where(name => Limits.IsClientRef(name) && !upstream.ContainsKey(name) && refs.ContainsKey(name)))
            results.Add(new(name, MirrorRefChange.Kept, refs[name], refs[name]));

        Limits.Require(refs.Count <= Limits.RefCount, "ref-count");
        bool changed = received.Objects.Count > 0 || !SameRefs(refs, repository.Refs);
        if (changed)
        {
            repository.Apply(objects, refs, cancellationToken);
            if (repository.LooseObjectCount >= Repository.AutoRepackLooseObjects) repository.Repack(cancellationToken);
        }
        return new(results, received.Objects.Count);
    }

    private static bool SameRefs(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out string? value) && value == pair.Value);
}
