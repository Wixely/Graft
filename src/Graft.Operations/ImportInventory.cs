using System.Security.Cryptography;

namespace Graft.Operations;

public sealed record ImportRef(string Name, string ObjectId);
public sealed record ImportExclusion(string Area, string Reason);
public sealed record GitBundleInventory(string Format, string Sha256, long Bytes, ImportRef[] Refs, ImportExclusion[] Excluded)
{
    private static readonly ImportExclusion[] RepositoryOnlyExclusions =
    [
        new("namespaces", "Groups, subgroups and project settings are not contained in a Git repository."),
        new("identities", "Users, identity mappings and memberships require a later metadata importer."),
        new("collaboration", "Issues, merge requests, reviews, discussions, labels, milestones and boards are excluded."),
        new("wiki-and-uploads", "Wiki repositories, uploads, releases and release assets require separate inventory and transfer."),
        new("ci-cd", "Pipeline history, jobs, logs, artifacts, caches, variables, schedules and environments are excluded."),
        new("packages", "LFS objects, package feeds and container registry content are excluded."),
        new("integrations", "Hooks, deploy keys, access tokens, OAuth applications and external integrations are excluded."),
        new("audit-and-analytics", "Audit events, approvals, security findings, analytics and usage data are excluded.")
    ];

    public static GitBundleInventory Read(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        string path = Path.GetFullPath(bundlePath);
        FileInfo info = new(path);
        if (!info.Exists) throw new FileNotFoundException("Git bundle was not found.", path);
        if (info.Length > 1024L * 1024 * 1024) throw new InvalidDataException("Git bundle exceeds the 1 GiB inventory limit.");
        string hash;
        using (FileStream stream = info.OpenRead()) hash = Convert.ToHexStringLower(SHA256.HashData(stream));

        using FileStream input = info.OpenRead();
        string? signature = ReadAsciiLine(input);
        string format = signature switch
        {
            "# v2 git bundle" => "v2",
            "# v3 git bundle" => "v3",
            _ => throw new InvalidDataException("Unsupported Git bundle signature.")
        };
        List<ImportRef> refs = [];
        int headerBytes = signature!.Length + 1;
        string? line;
        while ((line = ReadAsciiLine(input)) is not null)
        {
            headerBytes += line.Length + 1;
            if (headerBytes > 1024 * 1024) throw new InvalidDataException("Git bundle header exceeds 1 MiB.");
            if (line.Length == 0) break;
            if (line[0] is '-' or '@') continue;
            int separator = line.IndexOf(' ');
            string name = separator < 0 ? "" : line[(separator + 1)..];
            if (separator != 40 || !Graft.Git.Limits.IsId(line[..separator]) ||
                !(name == "HEAD" || Graft.Git.Limits.IsRef(name)))
                throw new InvalidDataException("Git bundle contains an invalid reference header.");
            if (refs.Any(item => item.Name == name))
                throw new InvalidDataException("Git bundle contains a duplicate reference.");
            refs.Add(new(name, line[..separator]));
            if (refs.Count > 4096) throw new InvalidDataException("Git bundle contains too many references.");
        }
        if (line is null || refs.Count == 0) throw new InvalidDataException("Git bundle has no complete reference inventory.");
        return new(format, hash, info.Length, refs.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            RepositoryOnlyExclusions.ToArray());
    }

    private static string? ReadAsciiLine(Stream input)
    {
        List<byte> bytes = [];
        while (true)
        {
            int value = input.ReadByte();
            if (value < 0) return bytes.Count == 0 ? null : throw new InvalidDataException("Git bundle header is truncated.");
            if (value == '\n') break;
            if (value > 0x7f || bytes.Count >= 1024 * 1024)
                throw new InvalidDataException("Git bundle header is not bounded ASCII.");
            if (value != '\r') bytes.Add((byte)value);
        }
        return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
    }
}
