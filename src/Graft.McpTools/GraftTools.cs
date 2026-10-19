using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Graft.Contracts;
using ModelContextProtocol.Server;

namespace Graft.Mcp;

/// <summary>
/// The tools this server offers. Every one is a thin call to Graft's ordinary HTTP API with the configured
/// token, so what an agent can do here is exactly what that token's owner can do, and nothing is reachable
/// that would not also be reachable with curl.
///
/// <para>
/// Results are built as JSON node trees rather than serialized from anonymous types. That is not a style
/// preference: reflection-based serialization is off in this project so a trimmed or native build cannot
/// silently lose shapes, and an anonymous type would fail there while working perfectly under a debugger.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class GraftTools
{
    [McpServerTool(Name = "graft_list_projects")]
    [Description("Lists the projects this token can see: the ones its owner owns or is a member of. Does not list other people's public projects; use graft_browse for those.")]
    public static async Task<string> ListProjects(GraftClient graft, CancellationToken cancellationToken)
    {
        // The GitLab-shaped listing, not Graft's own /api/projects. That one authenticates a browser session
        // cookie and refuses a token outright, so this tool could never have worked against it.
        (ProjectResponse[]? projects, string? error) =
            await graft.GetAsync("api/v4/projects", ApiJson.Default.ProjectResponseArray, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["count"] = projects!.Length,
            ["projects"] = Array(projects, Summarise)
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_get_project")]
    [Description("Reads one project by its path, such as alice/widgets, or by its numeric id.")]
    public static async Task<string> GetProject(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (ProjectResponse? found, string? error) =
            await graft.GetAsync("api/v4/projects/" + Encode(project), ApiJson.Default.ProjectResponse, cancellationToken);
        if (error is not null) return Failure(error);

        JsonObject result = Summarise(found!);
        (PageStateResponse? page, string? pageError) = await graft.GetAsync(
            "api/graft/projects/" + Encode(project) + "/page", ApiJson.Default.PageStateResponse, cancellationToken);
        result["page_published"] = pageError is null && page is not null ? page.Published : null;
        return result.ToJsonString();
    }

    [McpServerTool(Name = "graft_browse")]
    [Description("Searches the server's public listing: projects that are public, or that publish a page. This is the anonymous view, so it never reveals a private or unlisted project even though the token could see some of them.")]
    public static async Task<string> Browse(
        GraftClient graft,
        [Description("Optional text to match against name, path, owner or description. Empty lists everything published.")] string search,
        CancellationToken cancellationToken)
    {
        string path = "api/browse" + (string.IsNullOrWhiteSpace(search) ? "" : "?search=" + Uri.EscapeDataString(search.Trim()));
        (BrowseResponse? listing, string? error) = await graft.GetAsync(path, ApiJson.Default.BrowseResponse, cancellationToken);
        if (error is not null)
            return Failure(error + " A 404 here usually means the operator has not enabled public browsing.");

        return new JsonObject
        {
            ["total"] = listing!.Total,
            ["projects"] = Array(listing.Projects, entry => new JsonObject
            {
                ["name"] = entry.Name,
                ["path"] = entry.Path,
                ["description"] = entry.Description,
                ["visibility"] = entry.Visibility,
                ["source_public"] = entry.SourcePublic,
                ["web_url"] = entry.WebUrl
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_list_branches")]
    [Description("Lists a project's branches and the commit each points at.")]
    public static async Task<string> ListBranches(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (BranchResponse[]? branches, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project) + "/repository/branches", ApiJson.Default.BranchResponseArray, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["count"] = branches!.Length,
            ["branches"] = Array(branches, branch => new JsonObject
            {
                ["name"] = branch.Name,
                ["default"] = branch.Default,
                ["commit"] = branch.Commit.Id,
                ["title"] = branch.Commit.Title
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_list_commits")]
    [Description("Lists recent commits on a branch or revision, newest first.")]
    public static async Task<string> ListCommits(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        [Description("Branch or revision. Empty uses the project's default branch.")] string reference,
        CancellationToken cancellationToken)
    {
        string path = "api/v4/projects/" + Encode(project) + "/repository/commits" +
            (string.IsNullOrWhiteSpace(reference) ? "" : "?ref_name=" + Uri.EscapeDataString(reference.Trim()));
        (CommitResponse[]? commits, string? error) =
            await graft.GetAsync(path, ApiJson.Default.CommitResponseArray, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["count"] = commits!.Length,
            ["commits"] = Array(commits, commit => new JsonObject
            {
                ["id"] = commit.Id,
                ["short_id"] = commit.ShortId,
                ["title"] = commit.Title,
                ["author"] = commit.AuthorName,
                ["committed"] = commit.CommittedDate.ToString("O", CultureInfo.InvariantCulture),
                ["web_url"] = commit.WebUrl
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_list_tree")]
    [Description("Lists the files and directories at a path inside a project.")]
    public static async Task<string> ListTree(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        [Description("Directory inside the repository. Empty means the root.")] string path,
        [Description("Branch or revision. Empty uses the default branch.")] string reference,
        CancellationToken cancellationToken)
    {
        string query = "api/v4/projects/" + Encode(project) + "/repository/tree?path=" +
            Uri.EscapeDataString(path?.Trim() ?? "") + "&ref=" + Uri.EscapeDataString(Revision(reference));
        (TreeEntryResponse[]? entries, string? error) =
            await graft.GetAsync(query, ApiJson.Default.TreeEntryResponseArray, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["count"] = entries!.Length,
            ["entries"] = Array(entries, entry => new JsonObject
            {
                ["name"] = entry.Name,
                ["path"] = entry.Path,
                ["type"] = entry.Type
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_read_file")]
    [Description("Reads one text file from a project. Binary files and very large files are reported rather than returned.")]
    public static async Task<string> ReadFile(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        [Description("File path inside the repository, such as README.md.")] string file,
        [Description("Branch or revision. Empty uses the default branch.")] string reference,
        CancellationToken cancellationToken)
    {
        // The file route requires a ref and refuses an empty one, so "the default branch" has to be spelled
        // as HEAD rather than left out. The tree and commit routes are more forgiving, which is exactly why
        // this was the tool that broke while the others looked fine.
        string query = "api/v4/projects/" + Encode(project) + "/repository/files/" + Uri.EscapeDataString(file ?? "") +
            "?ref=" + Uri.EscapeDataString(Revision(reference));
        (FileResponse? found, string? error) = await graft.GetAsync(query, ApiJson.Default.FileResponse, cancellationToken);
        if (error is not null) return Failure(error);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(found!.Content); }
        catch (FormatException) { return Failure("Graft returned content this client could not decode."); }

        // Refused rather than mangled. A tool that returns replacement characters for a binary file teaches
        // the agent that the file is text, and the next thing it does with that is wrong.
        string? text = Decode(bytes);
        if (text is null)
            return new JsonObject
            {
                ["path"] = found.FilePath,
                ["size"] = found.Size,
                ["readable"] = false,
                ["reason"] = "This file is not UTF-8 text."
            }.ToJsonString();

        return new JsonObject
        {
            ["path"] = found.FilePath,
            ["size"] = found.Size,
            ["readable"] = true,
            ["ref"] = found.Ref,
            ["content"] = text
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_create_project")]
    [Description("Creates a project owned by the token's account. Refused unless writes are allowed on this server.")]
    public static async Task<string> CreateProject(
        GraftClient graft,
        [Description("Path for the new project, 2 to 39 letters or numbers separated by single dots, underscores or hyphens.")] string path,
        [Description("Human readable name. Empty uses the path.")] string name,
        [Description("Description. May be empty.")] string description,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "creating a project");

        JsonObject body = new() { ["path"] = path };
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name;
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post, "api/v4/projects", body, cancellationToken);
        return ok
            ? new JsonObject { ["created"] = true, ["path"] = path }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
    }

    [McpServerTool(Name = "graft_set_visibility")]
    [Description("Sets who can read a whole project: private, unlisted, or public. Public and unlisted expose every file and the history, and allow cloning without an account. Refused unless writes are allowed on this server.")]
    public static async Task<string> SetVisibility(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        [Description("One of private, unlisted or public.")] string visibility,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "changing who can see a project");
        if (visibility is not ("private" or "unlisted" or "public"))
            return Failure("visibility must be private, unlisted or public.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/v4/projects/" + Encode(project), new JsonObject { ["visibility"] = visibility }, cancellationToken);
        return ok
            ? new JsonObject { ["project"] = project, ["visibility"] = visibility }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
    }

    [McpServerTool(Name = "graft_update_project")]
    [Description("Changes a project's name, description, or the branch it opens at. Only the values given are changed; leave one empty to leave it alone. None of these move the project or change any link to it. Refused unless writes are allowed on this server.")]
    public static async Task<string> UpdateProject(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("New human readable name. Empty leaves it unchanged.")] string name,
        [Description("New description. Empty leaves it unchanged; pass a single space to clear it.")] string description,
        [Description("Branch the project should open at. Empty leaves it unchanged; the word 'default' clears the preference and lets the repository decide.")] string defaultBranch,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "changing a project's name, description or default branch");

        JsonObject body = [];
        if (!string.IsNullOrEmpty(name)) body["name"] = name;
        // A single space means "make it empty", because an empty argument has to mean "leave this alone"
        // for a tool whose parameters are all required strings.
        if (!string.IsNullOrEmpty(description)) body["description"] = description.Trim();
        if (!string.IsNullOrEmpty(defaultBranch))
            body["default_branch"] = defaultBranch == "default" ? null : defaultBranch;
        if (body.Count == 0) return Failure("Give at least one of name, description or default branch.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/v4/projects/" + Encode(project), body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));

        JsonObject result = new() { ["project"] = project };
        foreach (KeyValuePair<string, JsonNode?> change in body) result[change.Key] = change.Value?.DeepClone();
        return result.ToJsonString();
    }

    [McpServerTool(Name = "graft_whoami")]
    [Description("Reports the account this server's credential belongs to, and whether it is an administrator. Use it to check what the agent is allowed to do before trying something.")]
    public static async Task<string> WhoAmI(GraftClient graft, CancellationToken cancellationToken)
    {
        if (graft.Anonymous)
            return new JsonObject
            {
                ["authenticated"] = false,
                ["note"] = "This server was given no credential, so Graft is answering as it would any stranger: only public projects are visible."
            }.ToJsonString();

        (UserResponse? user, string? error) = await graft.GetAsync("api/v4/user", ApiJson.Default.UserResponse, cancellationToken);
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["authenticated"] = true,
            ["username"] = user!.Username,
            ["name"] = user.Name,
            ["state"] = user.State,
            ["web_url"] = user.WebUrl,
            ["writes_allowed"] = graft.AllowWrites
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_set_page_published")]
    [Description("Publishes or withdraws a project's curated page. A published page lists the project for everyone even when its repository stays private, and it exposes no source. Refused unless writes are allowed on this server.")]
    public static async Task<string> SetPagePublished(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        [Description("True to publish the page, false to withdraw it.")] bool published,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "publishing or withdrawing a project page");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/graft/projects/" + Encode(project) + "/page",
            new JsonObject { ["published"] = published }, cancellationToken);
        return ok
            ? new JsonObject { ["project"] = project, ["page_published"] = published }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
    }

    /// <summary>
    /// A refusal that names what would permit the action. An agent told only "not allowed"
    /// tends to try again a different way; one told what the operator would have to change stops.
    /// </summary>
    private static string Refused(GraftClient graft, string action) => new JsonObject
    {
        ["error"] = true,
        ["refused"] = "This Graft MCP server is read-only, so " + action + " is not available.",
        ["remedy"] = graft.WriteRemedy
    }.ToJsonString();

    private static string Failure(string message) =>
        new JsonObject { ["error"] = true, ["message"] = message }.ToJsonString();

    private static JsonObject Summarise(ProjectResponse project) => new()
    {
        ["id"] = project.Id,
        ["path"] = project.PathWithNamespace,
        ["name"] = project.Name,
        ["description"] = project.Description,
        ["visibility"] = project.Visibility,
        ["default_branch"] = project.DefaultBranch,
        ["clone_url"] = project.HttpUrlToRepo,
        ["web_url"] = project.WebUrl
    };

    /// <summary>
    /// Builds an array of objects. The cast to <see cref="JsonNode"/> is load-bearing: without it this
    /// binds the generic <c>Add&lt;T&gt;</c>, which reflects over T to make a JsonValue and is neither trim
    /// nor AOT safe. The analyzers in this project fail the build on that, which is how it was caught.
    /// </summary>
    private static JsonArray Array<T>(IReadOnlyList<T> items, Func<T, JsonObject> project)
    {
        JsonArray array = [];
        foreach (T item in items) array.Add((JsonNode)project(item));
        return array;
    }

    private static string Encode(string project) =>
        Uri.EscapeDataString((project ?? "").Trim());

    /// <summary>The revision to ask for. Empty means the branch the project opens at, which the engine
    /// resolves from HEAD, so the caller never has to know its name.</summary>
    private static string Revision(string? reference) =>
        string.IsNullOrWhiteSpace(reference) ? "HEAD" : reference.Trim();

    /// <summary>Decodes strict UTF-8, or returns null. Strict so that a binary file is detected rather than
    /// turned into replacement characters that read like text.</summary>
    private static string? Decode(byte[] bytes)
    {
        if (System.Array.IndexOf(bytes, (byte)0) >= 0) return null;
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }
}
