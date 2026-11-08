using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graft.Core;

/// <summary>Where a project's static site lives in the repository, and which file answers for a directory.</summary>
public sealed record PageSite(string Root, string Index);

/// <summary>How much of a commit's author a page's history shows.</summary>
public enum PageAuthors
{
    /// <summary>Messages and dates only. The default: who wrote a commit is not implied by showing that it
    /// was written.</summary>
    None = 0,
    /// <summary>Author names, never addresses.</summary>
    Names = 1,
    /// <summary>Names and email addresses, as Git recorded them.</summary>
    Emails = 2
}

/// <summary>
/// The default branch's recent history, as a page shows it: the messages, the dates, and as much of the
/// author as the config allowed. Never a diff, a changed-file list or a branch name; those print what the
/// page did not name.
/// </summary>
public sealed record PageHistory(int Commits, PageAuthors Authors);

/// <summary>
/// What a published project page offers. Every field is optional and so is the whole file; what is absent
/// is absent, never defaulted into something that exposes more.
/// </summary>
public sealed record PageSettings(
    string? Title,
    string? Description,
    string? Image,
    string? Readme,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Features,
    PageSite? Site,
    /// <summary>Whether the page lists the default branch's directory structure: every path and its kind,
    /// with content only for the files <see cref="Files"/> admits.</summary>
    bool Tree = false,
    /// <summary>The history the page shows, or null for none.</summary>
    PageHistory? History = null,
    /// <summary>Whether the page's readers may read the project's issues and their comments. Read only,
    /// and never a confidential issue.</summary>
    bool Issues = false,
    /// <summary>Whether the page's readers may read the project's releases and download their files.</summary>
    bool Releases = false)
{
    /// <summary>
    /// What a project with no config in its repository offers: nothing but the fact that it exists. Deny by
    /// default is the whole point of the surface, so the empty case cannot be "show everything".
    /// </summary>
    public static PageSettings Empty { get; } = new(null, null, null, null, [], [], null);

    /// <summary>Whether this page offers anything at all beyond its name and description.</summary>
    public bool OffersContent => Files.Count > 0 || Site is not null || Tree || History is not null || Issues || Releases;

    /// <summary>What this page grants beyond its own surface, in the shape the authorization decision takes.</summary>
    public PageReach Reach => new(Issues, Releases);
}

/// <summary>The outcome of reading a config: settings, or the reason there are none.</summary>
public sealed record PageConfigurationResult(PageSettings? Settings, string? Problem)
{
    public bool Ok => Settings is not null;
}

/// <summary>
/// Reads <c>.graft/project.json</c> from a repository and turns it into the settings a page is served
/// under.
///
/// <para>
/// The config may only narrow. It cannot publish: publishing lives in the database and needs Maintainer,
/// while authoring repository content needs only Developer, so a config able to publish would let a
/// Developer expose a private project by pushing a commit.
/// </para>
///
/// <para>
/// An invalid config is not a page with default settings. It unpublishes the page, because a parse failure
/// means nobody has said what may be served, and the safe reading of "nobody has said" is "nothing".
/// </para>
/// </summary>
public static class PageConfiguration
{
    /// <summary>Where the config lives. Read from the default branch, never from an arbitrary ref.</summary>
    public const string Path = ".graft/project.json";

    /// <summary>The directory the config lives in, which is never itself servable at any visibility.</summary>
    public const string ReservedDirectory = ".graft";

    /// <summary>Largest config accepted. A page config is a few lines; anything larger is a mistake or an attack.</summary>
    public const int MaximumBytes = 64 * 1024;

    /// <summary>Most patterns one config may carry, so matching stays bounded however the file was authored.</summary>
    public const int MaximumPatterns = 100;

    /// <summary>Most commits a page's history may show. A page is a front door, not a log viewer.</summary>
    public const int MaximumHistoryCommits = 500;

    /// <summary>How many commits a history shows when the config asks for one without saying how many.</summary>
    public const int DefaultHistoryCommits = 100;

    /// <summary>
    /// The product surfaces a page may point its readers at, by GitLab's names. Naming anything else is an
    /// error, not a warning: a config claiming a feature that does not exist must fail loudly rather than
    /// appear to work. Naming one of these grants nothing. The page shows a link to the surface only to a
    /// reader who could already open it, so a page-only private project names features that nobody but its
    /// members will see, and a public project's page links its visitors onward.
    /// </summary>
    public static IReadOnlyList<string> KnownFeatures { get; } =
        ["issues", "merge_requests", "boards", "milestones", "wiki", "releases", "snippets"];

    /// <summary>
    /// Parses the config bytes. Returns the settings, or the reason the page must not be served.
    /// </summary>
    public static PageConfigurationResult Read(byte[]? content)
    {
        // No file is not an error. A project that published a page and never wrote a config has said
        // nothing about what to serve, which is the empty page, not a refusal.
        if (content is null || content.Length == 0) return new(PageSettings.Empty, null);
        if (content.Length > MaximumBytes) return new(null, "The page config is larger than 64 KB.");

        JsonDocument document;
        try { document = JsonDocument.Parse(content); }
        catch (JsonException error) { return new(null, "The page config is not valid JSON: " + error.Message); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new(null, "The page config must be a JSON object.");

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                if (property.Name is not ("version" or "page"))
                    return new(null, "The page config has a field Graft does not understand: " + property.Name);

            if (document.RootElement.TryGetProperty("version", out JsonElement version))
            {
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
                    return new(null, "The page config version must be 1.");
            }

            if (!document.RootElement.TryGetProperty("page", out JsonElement page))
                return new(PageSettings.Empty, null);
            if (page.ValueKind != JsonValueKind.Object)
                return new(null, "The page config's page field must be an object.");

            return ReadPage(page);
        }
    }

    private static PageConfigurationResult ReadPage(JsonElement page)
    {
        foreach (JsonProperty property in page.EnumerateObject())
            if (property.Name is not ("title" or "description" or "image" or "readme" or "files" or "features" or "site"
                or "tree" or "history" or "issues" or "releases"))
                return new(null, "The page config has a field Graft does not understand: page." + property.Name);

        string? title = null;
        string? description = null;
        string? image = null;
        string? readme = null;
        List<string> files = [];
        List<string> features = [];
        PageSite? site = null;
        bool tree = false;
        PageHistory? history = null;
        bool issues = false;
        bool releases = false;

        // The switches. Each is a boolean and nothing else: a string "true" or a 1 is refused, because a
        // config that is lenient about what means "show this" will one day show it by accident.
        if (page.TryGetProperty("tree", out JsonElement treeValue))
        {
            if (!IsBoolean(treeValue)) return new(null, "page.tree must be true or false.");
            tree = treeValue.GetBoolean();
        }
        if (page.TryGetProperty("issues", out JsonElement issuesValue))
        {
            if (!IsBoolean(issuesValue)) return new(null, "page.issues must be true or false.");
            issues = issuesValue.GetBoolean();
        }
        if (page.TryGetProperty("releases", out JsonElement releasesValue))
        {
            if (!IsBoolean(releasesValue)) return new(null, "page.releases must be true or false.");
            releases = releasesValue.GetBoolean();
        }

        if (page.TryGetProperty("history", out JsonElement historyValue))
        {
            if (historyValue.ValueKind != JsonValueKind.Object) return new(null, "page.history must be an object.");
            foreach (JsonProperty property in historyValue.EnumerateObject())
                if (property.Name is not ("commits" or "authors"))
                    return new(null, "The page config has a field Graft does not understand: page.history." + property.Name);

            int commits = DefaultHistoryCommits;
            if (historyValue.TryGetProperty("commits", out JsonElement commitsValue))
            {
                if (commitsValue.ValueKind != JsonValueKind.Number || !commitsValue.TryGetInt32(out commits) ||
                    commits < 1 || commits > MaximumHistoryCommits)
                    return new(null, "page.history.commits must be a whole number from 1 to " + MaximumHistoryCommits + ".");
            }
            // Authors are hidden unless the config says otherwise. Showing that a commit happened and
            // showing who made it are different disclosures, and the second is the one with a person in it.
            PageAuthors authors = PageAuthors.None;
            if (historyValue.TryGetProperty("authors", out JsonElement authorsValue))
            {
                if (authorsValue.ValueKind != JsonValueKind.String) return new(null, "page.history.authors must be none, names or emails.");
                authors = (authorsValue.GetString() ?? "").Trim().ToLowerInvariant() switch
                {
                    "none" => PageAuthors.None,
                    "names" => PageAuthors.Names,
                    "emails" => PageAuthors.Emails,
                    _ => (PageAuthors)(-1)
                };
                if (authors < 0) return new(null, "page.history.authors must be none, names or emails.");
            }
            history = new(commits, authors);
        }

        if (page.TryGetProperty("title", out JsonElement titleValue))
        {
            if (titleValue.ValueKind != JsonValueKind.String) return new(null, "page.title must be text.");
            title = Trim(titleValue.GetString(), 200);
            if (title is null) return new(null, "page.title is longer than 200 characters.");
        }
        if (page.TryGetProperty("description", out JsonElement descriptionValue))
        {
            if (descriptionValue.ValueKind != JsonValueKind.String) return new(null, "page.description must be text.");
            description = Trim(descriptionValue.GetString(), 2_000);
            if (description is null) return new(null, "page.description is longer than 2000 characters.");
        }

        if (page.TryGetProperty("files", out JsonElement filesValue))
        {
            if (filesValue.ValueKind != JsonValueKind.Array) return new(null, "page.files must be a list of patterns.");
            foreach (JsonElement entry in filesValue.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) return new(null, "page.files must contain only patterns.");
                string pattern = PathMatcher.Normalize(entry.GetString());
                if (pattern.Length == 0)
                    return new(null, "page.files contains a pattern that is empty or leaves the repository: " + entry.GetString());
                if (pattern.Length > PathMatcher.MaximumPatternLength)
                    return new(null, "page.files contains a pattern longer than 200 characters.");
                // `.graft/` is never servable, at any visibility, so a config cannot name its own directory
                // and publish the allowlist that describes what it is hiding.
                if (IsReserved(pattern)) return new(null, "page.files may not name the " + ReservedDirectory + " directory.");
                files.Add(pattern);
                if (files.Count > MaximumPatterns) return new(null, "page.files has more than 100 patterns.");
            }
        }

        if (page.TryGetProperty("features", out JsonElement featuresValue))
        {
            if (featuresValue.ValueKind != JsonValueKind.Array) return new(null, "page.features must be a list of names.");
            foreach (JsonElement entry in featuresValue.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) return new(null, "page.features must contain only names.");
                string name = (entry.GetString() ?? "").Trim().ToLowerInvariant();
                if (!KnownFeatures.Contains(name))
                    return new(null, "page.features names a feature Graft does not have: " + name);
                features.Add(name);
            }
        }

        // image and readme must themselves be allowed. A path named in one field and absent from the
        // allowlist would otherwise be an implicit grant, and implicit grants are how a deny-by-default
        // surface stops being one.
        if (page.TryGetProperty("image", out JsonElement imageValue))
        {
            if (imageValue.ValueKind != JsonValueKind.String) return new(null, "page.image must be a path.");
            image = PathMatcher.Normalize(imageValue.GetString());
            if (image.Length == 0) return new(null, "page.image is not a path inside the repository.");
            if (!PathMatcher.IsAllowed(files, image)) return new(null, "page.image is not matched by page.files: " + image);
        }
        if (page.TryGetProperty("readme", out JsonElement readmeValue))
        {
            if (readmeValue.ValueKind != JsonValueKind.String) return new(null, "page.readme must be a path.");
            readme = PathMatcher.Normalize(readmeValue.GetString());
            if (readme.Length == 0) return new(null, "page.readme is not a path inside the repository.");
            if (!PathMatcher.IsAllowed(files, readme)) return new(null, "page.readme is not matched by page.files: " + readme);
        }

        if (page.TryGetProperty("site", out JsonElement siteValue))
        {
            if (siteValue.ValueKind != JsonValueKind.Object) return new(null, "page.site must be an object.");
            foreach (JsonProperty property in siteValue.EnumerateObject())
                if (property.Name is not ("root" or "index"))
                    return new(null, "The page config has a field Graft does not understand: page.site." + property.Name);

            if (!siteValue.TryGetProperty("root", out JsonElement rootValue) || rootValue.ValueKind != JsonValueKind.String)
                return new(null, "page.site needs a root directory.");
            string root = PathMatcher.Normalize(rootValue.GetString());
            if (root.Length == 0) return new(null, "page.site.root is not a directory inside the repository.");
            if (IsReserved(root)) return new(null, "page.site.root may not be the " + ReservedDirectory + " directory.");

            string index = "index.html";
            if (siteValue.TryGetProperty("index", out JsonElement indexValue))
            {
                if (indexValue.ValueKind != JsonValueKind.String) return new(null, "page.site.index must be a file name.");
                index = PathMatcher.Normalize(indexValue.GetString());
                // A single segment: the index answers for a directory, so a path here would mean something
                // different at every depth and is more likely a misunderstanding than an intent.
                if (index.Length == 0 || index.Contains('/')) return new(null, "page.site.index must be a single file name.");
            }
            site = new(root, index);
        }

        return new(new PageSettings(title, description, image, readme, files, features, site, tree, history, issues, releases), null);
    }

    private static bool IsBoolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    /// <summary>Whether a page may serve this path, which is the only question the serving code asks.</summary>
    public static bool AllowsFile(PageSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string normalized = PathMatcher.Normalize(path);
        if (normalized.Length == 0 || IsReserved(normalized)) return false;
        return PathMatcher.IsAllowed(settings.Files, normalized);
    }

    /// <summary>Whether a path belongs to this page's static site, which is a separate grant from the file
    /// allowlist: a site's files are reachable on the pages origin and nowhere else.</summary>
    public static bool AllowsSiteFile(PageSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Site is null) return false;
        string normalized = PathMatcher.Normalize(path);
        if (normalized.Length == 0 || IsReserved(normalized)) return false;
        return PathMatcher.IsInside(settings.Site.Root, normalized);
    }

    private static bool IsReserved(string normalizedPath) =>
        normalizedPath.Equals(ReservedDirectory, StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith(ReservedDirectory + "/", StringComparison.OrdinalIgnoreCase);

    private static string? Trim(string? value, int maximum)
    {
        string text = (value ?? "").Trim();
        return text.Length > maximum ? null : text;
    }
}

/// <summary>
/// The content types a static site may be served as, taken from the file's extension rather than from its
/// bytes. A repository can name a file anything; letting the file decide its own type is how a page serves
/// script where the operator expected an image.
/// </summary>
public static class SiteContentTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".pdf"] = "application/pdf"
    };

    /// <summary>
    /// The type to serve a path as, or null when the extension is not on the list. Unknown is refused
    /// rather than sent as a default type, because a default is a decision about somebody else's bytes.
    /// </summary>
    public static string? For(string path)
    {
        string extension = System.IO.Path.GetExtension(path ?? "");
        return extension.Length != 0 && ByExtension.TryGetValue(extension, out string? type) ? type : null;
    }
}
