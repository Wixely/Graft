using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
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

    [McpServerTool(Name = "graft_import_project")]
    [Description("Creates a project owned by the token's account from another server's repository address, and optionally keeps it up to date from there. The import runs in the background; graft_mirror_status reports how it went. Updates never remove anything: a branch deleted upstream is kept, and one rewritten upstream keeps its old history under refs/graft-preserved/. Refused unless writes are allowed here and the server's operator turned on import by address.")]
    public static async Task<string> ImportProject(
        GraftClient graft,
        [Description("The repository's http or https address, such as https://example.com/owner/repository.git, with no password in it.")] string url,
        [Description("Path for the new project. Empty uses the address's last segment without .git.")] string path,
        [Description("Human readable name. Empty uses the path.")] string name,
        [Description("True to keep checking the address for changes after the import.")] bool keepUpToDate,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "importing a project");

        JsonObject body = new() { ["import_url"] = url, ["mirror"] = keepUpToDate };
        if (!string.IsNullOrWhiteSpace(path)) body["path"] = path;
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name;

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post, "api/v4/projects", body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        ProjectResponse? created = JsonSerializer.Deserialize(response, ApiJson.Default.ProjectResponse);
        JsonObject result = created is null ? new() : Summarise(created);
        result["import_status"] = created?.ImportStatus;
        result["mirror"] = created?.Mirror;
        return result.ToJsonString();
    }

    [McpServerTool(Name = "graft_mirror_status")]
    [Description("Reports a project's import from another server's address: where from, whether it is kept up to date, how the latest update went and when the next is due.")]
    public static async Task<string> MirrorStatus(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Get,
            "api/v4/projects/" + Encode(project) + "/mirror/pull", null, cancellationToken);
        // A project that exists but was never imported answers 404 too; say which, rather than "no such project".
        if (status == 404 && response.Contains("Pull Mirror Not Found", StringComparison.Ordinal))
            return Failure("This project was not imported from an address, so it has no mirror (Pull Mirror Not Found).");
        if (!ok) return Failure(GraftClient.Describe(status, response));
        PullMirrorResponse? mirror = JsonSerializer.Deserialize(response, ApiJson.Default.PullMirrorResponse);
        return mirror is null ? Failure("Graft answered with an empty mirror.") : Mirror(mirror).ToJsonString();
    }

    [McpServerTool(Name = "graft_update_mirror")]
    [Description("Asks for an imported project to be brought up to date from its address now, or a failed import to be tried again. Needs the Maintainer role. Refused unless writes are allowed on this server.")]
    public static async Task<string> UpdateMirror(
        GraftClient graft,
        [Description("The project path like alice/widgets, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "updating a mirror");
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/mirror/pull", new JsonObject(), cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        PullMirrorResponse? mirror = JsonSerializer.Deserialize(response, ApiJson.Default.PullMirrorResponse);
        return mirror is null ? new JsonObject { ["requested"] = true }.ToJsonString() : Mirror(mirror).ToJsonString();
    }

    private static JsonObject Mirror(PullMirrorResponse mirror) => new()
    {
        ["url"] = mirror.Url,
        ["kept_up_to_date"] = mirror.Enabled,
        ["status"] = mirror.UpdateStatus,
        ["last_successful_update_at"] = mirror.LastSuccessfulUpdateAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["last_changed_at"] = mirror.LastChangedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["last_error"] = mirror.LastError,
        ["consecutive_failures"] = mirror.ConsecutiveFailures,
        ["next_update_at"] = mirror.NextUpdateAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
    };

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

    [McpServerTool(Name = "graft_set_avatar")]
    [Description("Sets a project's avatar from an image you already have as base64. PNG, GIF, JPEG or WebP, at most 2 MB and 4096 pixels on a side. Refused unless writes are allowed on this server.")]
    public static async Task<string> SetAvatar(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The image file's bytes, base64 encoded.")] string imageBase64,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "setting a project's avatar");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(imageBase64 ?? ""); }
        catch (FormatException) { return Failure("The image must be base64 encoded."); }
        if (bytes.Length == 0) return Failure("The image is empty.");

        (bool ok, string response, int status) = await graft.SendBytesAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/avatar", bytes, "application/octet-stream", cancellationToken);
        return ok
            ? new JsonObject { ["project"] = project, ["avatar_set"] = true, ["bytes"] = bytes.Length }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
    }

    [McpServerTool(Name = "graft_list_members")]
    [Description("Lists who may work on a project and in what role. Only somebody who could change the membership can read it.")]
    public static async Task<string> ListMembers(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (ProjectMemberResponse[]? members, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project) + "/members", ApiJson.Default.ProjectMemberResponseArray, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["count"] = members!.Length,
            ["members"] = Array(members, member => new JsonObject
            {
                ["username"] = member.Username,
                ["role"] = member.Role
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_set_member")]
    [Description("Grants an account a role on a project, or changes the role it already has. Reporter can read, developer can push, maintainer can change the project's settings. Refused unless writes are allowed on this server.")]
    public static async Task<string> SetMember(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The account's username.")] string username,
        [Description("One of reporter, developer or maintainer.")] string role,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "changing who may work on a project");
        if (role is not ("reporter" or "developer" or "maintainer"))
            return Failure("role must be reporter, developer or maintainer.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/members",
            new JsonObject { ["username"] = username, ["role"] = role }, cancellationToken);
        return ok
            ? new JsonObject { ["project"] = project, ["username"] = username, ["role"] = role }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
    }

    [McpServerTool(Name = "graft_remove_member")]
    [Description("Takes away an account's role on a project. The owner cannot be removed this way. Refused unless writes are allowed on this server.")]
    public static async Task<string> RemoveMember(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The account's username.")] string username,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "removing somebody from a project");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Delete,
            "api/v4/projects/" + Encode(project) + "/members/" + Uri.EscapeDataString((username ?? "").Trim()),
            null, cancellationToken);
        return ok
            ? new JsonObject { ["project"] = project, ["username"] = username, ["removed"] = true }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
    }

    [McpServerTool(Name = "graft_set_banner")]
    [Description("Sets a project's banner, the wide image drawn behind its title and tab bar, from an image you already have as base64. PNG, GIF, JPEG or WebP, at most 2 MB and 4096 pixels on a side; about 1600 by 300 pixels suits it. Needs the Maintainer role. Refused unless writes are allowed on this server.")]
    public static async Task<string> SetBanner(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The image file's bytes, base64 encoded.")] string imageBase64,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "setting a project's banner");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(imageBase64 ?? ""); }
        catch (FormatException) { return Failure("The image must be base64 encoded."); }
        if (bytes.Length == 0) return Failure("The image is empty.");

        (bool ok, string response, int status) = await graft.SendBytesAsync(HttpMethod.Put,
            "api/graft/projects/" + Encode(project) + "/banner", bytes, "application/octet-stream", cancellationToken);
        if (!ok) return Failure(Explain(status, response));
        return new JsonObject { ["project"] = project, ["banner_set"] = true, ["bytes"] = bytes.Length, ["banner_url"] = Field(response, "banner_url") }.ToJsonString();
    }

    [McpServerTool(Name = "graft_remove_banner")]
    [Description("Removes a project's banner, so its header goes back to the plain one. Needs the Maintainer role. Refused unless writes are allowed on this server.")]
    public static async Task<string> RemoveBanner(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "removing a project's banner");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Delete,
            "api/graft/projects/" + Encode(project) + "/banner", null, cancellationToken);
        if (status == 404 && response.Contains("Banner Not Found", StringComparison.Ordinal))
            return Failure("The project has no banner to remove.");
        return ok
            ? new JsonObject { ["project"] = project, ["banner_removed"] = true }.ToJsonString()
            : Failure(Explain(status, response));
    }

    [McpServerTool(Name = "graft_list_tags")]
    [Description("Lists a project's tags and the commit each one names.")]
    public static async Task<string> ListTags(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (TagResponse[]? tags, long total, bool truncated, string? error) = await graft.GetListAsync(
            "api/v4/projects/" + Encode(project) + "/repository/tags?per_page=100", ApiJson.Default.TagResponseArray, cancellationToken);
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["count"] = tags!.Length,
            ["total"] = total,
            ["truncated"] = truncated,
            ["tags"] = Array(tags, tag => new JsonObject
            {
                ["name"] = tag.Name,
                ["commit"] = tag.Commit.Id,
                ["title"] = tag.Commit.Title
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_create_tag")]
    [Description("Creates a lightweight tag pointing at a branch, another tag or a commit. A tag that already exists is refused rather than moved. Needs the permission to push. Refused unless writes are allowed on this server.")]
    public static async Task<string> CreateTag(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The new tag's name, such as v1.2.0.")] string tag,
        [Description("The branch, tag or commit id to tag. Empty tags the branch the project opens at.")] string reference,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "creating a tag");
        if (string.IsNullOrWhiteSpace(tag)) return Failure("Give the tag a name.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/repository/tags",
            new JsonObject { ["tag_name"] = tag.Trim(), ["ref"] = Revision(reference) }, cancellationToken);
        if (!ok) return Failure(Explain(status, response));
        TagResponse? created = Read(response, ApiJson.Default.TagResponse);
        return new JsonObject { ["project"] = project, ["tag"] = tag.Trim(), ["commit"] = created?.Commit.Id }.ToJsonString();
    }

    [McpServerTool(Name = "graft_delete_tag")]
    [Description("Deletes a tag. The commit it named stays in the history. A release that sat on the tag stays too, reported as having lost it. Needs the permission to push. Refused unless writes are allowed on this server.")]
    public static async Task<string> DeleteTag(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The tag's name.")] string tag,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "deleting a tag");
        if (string.IsNullOrWhiteSpace(tag)) return Failure("Name the tag to delete.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Delete,
            "api/v4/projects/" + Encode(project) + "/repository/tags/" + Uri.EscapeDataString(tag.Trim()), null, cancellationToken);
        return ok
            ? new JsonObject { ["project"] = project, ["tag"] = tag.Trim(), ["deleted"] = true }.ToJsonString()
            : Failure(Explain(status, response));
    }

    [McpServerTool(Name = "graft_get_review_rules")]
    [Description("Reads what a merge request needs before it can be merged: how many approvals, and whether a code owner must approve each path a CODEOWNERS file names.")]
    public static async Task<string> GetReviewRules(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (ProjectApprovalsResponse? rules, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project) + "/approvals", ApiJson.Default.ProjectApprovalsResponse, cancellationToken);
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["project"] = project,
            ["approvals_required"] = rules!.ApprovalsBeforeMerge,
            ["require_code_owner_approval"] = rules.RequireCodeOwnerApproval,
            ["note"] = "A push supersedes earlier approvals, and an author cannot approve their own; neither is a setting."
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_set_review_rules")]
    [Description("Sets what a merge request needs before it can be merged. Only the values given are changed; leave one empty to leave it alone. Needs the Maintainer role. Refused unless writes are allowed on this server.")]
    public static async Task<string> SetReviewRules(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("How many current approvals a merge needs, 0 to 10; 0 removes the gate. Empty leaves it unchanged.")] string approvalsRequired,
        [Description("true or false: whether a code owner must approve every changed path a CODEOWNERS file names. Empty leaves it unchanged.")] string requireCodeOwnerApproval,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "changing a project's review rules");

        JsonObject body = [];
        if (!string.IsNullOrWhiteSpace(approvalsRequired))
        {
            if (!int.TryParse(approvalsRequired.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count > 10)
                return Failure("approvalsRequired must be a whole number from 0 to 10.");
            body["approvals_before_merge"] = count;
        }
        if (!string.IsNullOrWhiteSpace(requireCodeOwnerApproval))
        {
            string flag = requireCodeOwnerApproval.Trim().ToLowerInvariant();
            if (flag is not ("true" or "false")) return Failure("requireCodeOwnerApproval must be true or false.");
            body["require_code_owner_approval"] = flag == "true";
        }
        if (body.Count == 0) return Failure("Give at least one of approvalsRequired or requireCodeOwnerApproval.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/v4/projects/" + Encode(project) + "/approvals", body, cancellationToken);
        if (!ok) return Failure(Explain(status, response));
        JsonObject result = new() { ["project"] = project };
        foreach (KeyValuePair<string, JsonNode?> change in body) result[change.Key] = change.Value?.DeepClone();
        return result.ToJsonString();
    }

    [McpServerTool(Name = "graft_list_activity")]
    [Description("Reads a project's audit trail, newest first: who changed its settings, membership, visibility and so on, and when. Needs the Maintainer role.")]
    public static async Task<string> ListActivity(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("Which page of 50 entries, from 1. Values below 1 read the first.")] int page,
        CancellationToken cancellationToken)
    {
        int chosen = Math.Max(1, page);
        (AuditEventResponse[]? events, long total, bool truncated, string? error) = await graft.GetListAsync(
            "api/v4/projects/" + Encode(project) + "/audit_events?per_page=50&page=" + chosen.ToString(CultureInfo.InvariantCulture),
            ApiJson.Default.AuditEventResponseArray, cancellationToken);
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["page"] = chosen,
            ["total"] = total,
            ["more"] = truncated || total > (long)chosen * 50,
            ["events"] = Array(events!, entry => new JsonObject
            {
                ["at"] = entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                ["by"] = entry.Details.AuthorName,
                ["action"] = entry.Details.Action,
                ["detail"] = entry.Details.CustomMessage
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_list_webhooks")]
    [Description("Lists a project's webhooks: the address each calls, the events it asked for and how its last delivery went. Secrets are never shown. Needs the Maintainer role, and a server whose operator turned webhooks on.")]
    public static async Task<string> ListWebhooks(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Get,
            "api/v4/projects/" + Encode(project) + "/hooks", null, cancellationToken);
        if (!ok) return Failure(ExplainHooks(status, response));
        WebhookResponse[] hooks = Read(response, ApiJson.Default.WebhookResponseArray) ?? [];
        return new JsonObject
        {
            ["count"] = hooks.Length,
            ["webhooks"] = Array(hooks, hook => new JsonObject
            {
                ["id"] = hook.Id,
                ["url"] = hook.Url,
                ["events"] = new JsonArray([.. hook.Events.Select(name => (JsonNode?)JsonValue.Create(name))]),
                ["created_by"] = hook.CreatedBy,
                ["last_status"] = hook.LastStatus,
                ["last_error"] = hook.LastError
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_add_webhook")]
    [Description("Adds a webhook: Graft will POST to the address when the chosen events happen, signed with the secret as X-Graft-Signature (sha256= and an HMAC of the body). The secret is stored and never shown again. Needs the Maintainer role, and a server whose operator turned webhooks on. Refused unless writes are allowed on this server.")]
    public static async Task<string> AddWebhook(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("An absolute http or https address, with no credentials in it.")] string url,
        [Description("The signing secret, 16 to 256 characters. The receiver needs the same value.")] string secret,
        [Description("Comma-separated events: any of push, issue, merge_request, comment.")] string events,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "adding a webhook");
        string[] names = (events ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return Failure("Name at least one event: push, issue, merge_request or comment.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/hooks",
            new JsonObject
            {
                ["url"] = (url ?? "").Trim(),
                ["secret"] = secret ?? "",
                ["events"] = new JsonArray([.. names.Select(name => (JsonNode?)JsonValue.Create(name))])
            }, cancellationToken);
        if (!ok) return Failure(ExplainHooks(status, response));
        WebhookResponse? hook = Read(response, ApiJson.Default.WebhookResponse);
        return new JsonObject { ["project"] = project, ["id"] = hook?.Id, ["url"] = hook?.Url }.ToJsonString();
    }

    [McpServerTool(Name = "graft_remove_webhook")]
    [Description("Removes a webhook by its id, as graft_list_webhooks reports it. Needs the Maintainer role. Refused unless writes are allowed on this server.")]
    public static async Task<string> RemoveWebhook(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The webhook's id.")] long id,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "removing a webhook");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Delete,
            "api/v4/projects/" + Encode(project) + "/hooks/" + id.ToString(CultureInfo.InvariantCulture), null, cancellationToken);
        return ok
            ? new JsonObject { ["project"] = project, ["id"] = id, ["removed"] = true }.ToJsonString()
            : Failure(ExplainHooks(status, response));
    }

    [McpServerTool(Name = "graft_transfer_project")]
    [Description("Gives a project to another account. Its path moves under the new owner's name, so the old clone address and links stop working; the previous owner stays on as a Maintainer. Only the owner or an administrator may. Refused unless an administrator has allowed both writes and deleting and transferring projects.")]
    public static async Task<string> TransferProject(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The username of the account that will own it.")] string newOwner,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowDestructive) return RefusedDestructive(graft, "transferring a project");
        if (string.IsNullOrWhiteSpace(newOwner)) return Failure("Name the account that will own the project.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/v4/projects/" + Encode(project) + "/transfer", new JsonObject { ["namespace"] = newOwner.Trim() }, cancellationToken);
        if (!ok) return Failure(Explain(status, response));
        ProjectResponse? moved = Read(response, ApiJson.Default.ProjectResponse);
        return new JsonObject
        {
            ["transferred"] = true,
            ["path"] = moved?.PathWithNamespace,
            ["web_url"] = moved?.WebUrl,
            ["note"] = "The project's address changed; use the new path from now on."
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_delete_project")]
    [Description("Deletes a project: its repository, issues, merge requests, wiki, releases and everything else, immediately and permanently. There is no undo. Only the owner or an administrator may. Give the project's full path again as confirm, exactly as graft_get_project reports it. Refused unless an administrator has allowed both writes and deleting and transferring projects.")]
    public static async Task<string> DeleteProject(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The project's full path, such as alice/widgets, typed out again to confirm.")] string confirm,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowDestructive) return RefusedDestructive(graft, "deleting a project");

        // Confirmed against what the server says the project is, not against the argument: an id or a slug
        // repeated twice would otherwise confirm nothing about which project is about to go.
        (ProjectResponse? target, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project), ApiJson.Default.ProjectResponse, cancellationToken);
        if (error is not null) return Failure(error);
        if (!string.Equals((confirm ?? "").Trim(), target!.PathWithNamespace, StringComparison.Ordinal))
            return Failure("Not deleted. confirm must be the project's full path, " + target.PathWithNamespace + ".");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Delete,
            "api/v4/projects/" + target.Id.ToString(CultureInfo.InvariantCulture), null, cancellationToken);
        return ok
            ? new JsonObject { ["deleted"] = true, ["path"] = target.PathWithNamespace }.ToJsonString()
            : Failure(Explain(status, response));
    }

    [McpServerTool(Name = "graft_read_commit")]
    [Description("Reads what one commit changed: the files it touched and the lines it added and removed. Large or binary files are reported rather than printed.")]
    public static async Task<string> ReadCommit(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The full 40-character commit id, as graft_list_commits reports it.")] string commit,
        [Description("For a merge, which parent to compare against: 0 is the first. Ignored for an ordinary commit.")] int parent,
        CancellationToken cancellationToken)
    {
        if (parent is < 0 or > 15) return Failure("parent must be between 0 and 15.");
        string commitPath = "api/v4/projects/" + Encode(project) + "/repository/commits/" + Uri.EscapeDataString((commit ?? "").Trim());
        (CommitResponse? found, string? missing) = await graft.GetAsync(commitPath, ApiJson.Default.CommitResponse, cancellationToken);
        if (missing is not null) return Failure(missing);
        // The diff is GitLab's array of files; whether it was cut short comes in a header.
        (GitLabDiffResponse[]? files, _, bool truncated, string? error) = await graft.GetListAsync(
            commitPath + "/diff" + (parent == 0 ? "" : "?parent=" + parent.ToString(CultureInfo.InvariantCulture)), ApiJson.Default.GitLabDiffResponseArray, cancellationToken);
        if (error is not null) return Failure(error);
        var diff = new { Truncated = truncated, Files = files! };

        return new JsonObject
        {
            ["commit"] = found!.Id,
            ["title"] = found.Title,
            ["author"] = found.AuthorName,
            ["compared_against"] = found.ParentIds.Count > parent ? found.ParentIds[parent] : null,
            ["added"] = files!.Sum(file => file.Added),
            ["removed"] = files!.Sum(file => file.Removed),
            // Said out loud: a truncated diff that looked complete would have an agent conclude a file was
            // untouched when the server simply stopped listing.
            ["truncated"] = diff.Truncated,
            ["files"] = Array(diff.Files, file => new JsonObject
            {
                ["path"] = file.NewPath,
                ["status"] = file.Status,
                ["added"] = file.Added,
                ["removed"] = file.Removed,
                ["binary"] = file.Binary,
                ["too_large"] = file.TooLarge,
                ["diff"] = file.Binary || file.TooLarge ? null : file.Diff
            })
        }.ToJsonString();
    }

    /// <summary>
    /// The hunks of one file as ordinary unified diff text. An agent reads diffs far better as the format
    /// every tool prints than as a tree of line objects it has to reassemble.
    /// </summary>
    private static string Unified(DiffFileResponse file)
    {
        StringBuilder text = new();
        foreach (DiffHunkResponse hunk in file.Hunks)
        {
            text.Append(hunk.Header).Append('\n');
            foreach (DiffLineResponse line in hunk.Lines)
            {
                // Kind is already the marker a unified diff prints: '+', '-' or a space for context.
                text.Append(line.Kind.Length == 1 ? line.Kind[0] : ' ');
                text.Append(line.Text).Append('\n');
                if (line.NoNewline) text.Append("\\ No newline at end of file\n");
            }
        }
        return text.ToString();
    }

    [McpServerTool(Name = "graft_write_file")]
    [Description("Writes one text file into a project and commits it, creating the file or replacing it. The branch must already exist unless the repository is empty. Refused unless writes are allowed on this server.")]
    public static async Task<string> WriteFile(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("File path inside the repository, such as docs/README.md.")] string file,
        [Description("The complete new contents of the file. This replaces the file rather than patching it.")] string content,
        [Description("Branch to commit on, such as main.")] string branch,
        [Description("Commit message.")] string message,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "writing a file");
        if (string.IsNullOrWhiteSpace(file)) return Failure("A file path is required.");
        if (string.IsNullOrWhiteSpace(branch)) return Failure("A branch is required.");
        if (string.IsNullOrWhiteSpace(message)) return Failure("A commit message is required.");

        // Sent as base64 so the bytes survive exactly. A file full of quotes, newlines or anything the
        // transport might normalise is the ordinary case for source code, not the exception.
        JsonObject body = new()
        {
            ["branch"] = branch,
            ["commit_message"] = message,
            ["encoding"] = "base64",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content ?? ""))
        };

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/repository/files/" + Uri.EscapeDataString(file.Trim()),
            body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));

        // The server answers with the commit it made, which is the thing an agent needs in order to report
        // what it did or to read the result back.
        try
        {
            JsonNode? commit = JsonNode.Parse(response);
            return new JsonObject
            {
                ["project"] = project,
                ["path"] = file,
                ["branch"] = commit?["branch"]?.GetValue<string>() ?? branch,
                ["commit_id"] = commit?["commit_id"]?.GetValue<string>(),
                ["created_branch"] = commit?["created_branch"]?.GetValue<bool>() ?? false
            }.ToJsonString();
        }
        catch (System.Text.Json.JsonException) { return new JsonObject { ["project"] = project, ["path"] = file, ["committed"] = true }.ToJsonString(); }
    }

    [McpServerTool(Name = "graft_delete_file")]
    [Description("Deletes one file from a project and commits the deletion. The file must exist on the branch; a directory left empty disappears with it. The history keeps the file. Refused unless writes are allowed on this server.")]
    public static async Task<string> DeleteFile(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("File path inside the repository, such as docs/old.md.")] string file,
        [Description("Branch to commit on, such as main. It must exist.")] string branch,
        [Description("Commit message.")] string message,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "deleting a file");
        if (string.IsNullOrWhiteSpace(file)) return Failure("A file path is required.");
        if (string.IsNullOrWhiteSpace(branch)) return Failure("A branch is required.");
        if (string.IsNullOrWhiteSpace(message)) return Failure("A commit message is required.");

        JsonObject body = new() { ["branch"] = branch, ["commit_message"] = message };
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Delete,
            "api/v4/projects/" + Encode(project) + "/repository/files/" + Uri.EscapeDataString(file.Trim()),
            body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        // GitLab's shape answers 204 with nothing, so the report is what was asked for rather than what the
        // server said; the commit itself is one graft_list_commits away.
        return new JsonObject { ["project"] = project, ["path"] = file, ["branch"] = branch, ["deleted"] = true }.ToJsonString();
    }

    [McpServerTool(Name = "graft_help")]
    [Description("Explains how this forge works: addressing a project, what bounds an agent, visibility, project pages, changing things, and limits. Call it with no topic for the list.")]
    public static string Help(
        [Description("One of: addressing, permissions, visibility, pages, writing, collaboration, limits. Empty returns the overview and the list of topics.")] string topic)
    {
        string name = (topic ?? "").Trim();
        if (name.Length == 0)
            return new JsonObject
            {
                ["overview"] = GraftGuide.Instructions,
                ["topics"] = Array([.. GraftGuide.Topics.Keys], key => new JsonObject { ["topic"] = key })
            }.ToJsonString();

        if (!GraftGuide.Topics.TryGetValue(name, out string? document))
            return Failure("No such topic. Try one of: " + string.Join(", ", GraftGuide.Topics.Keys) + ".");

        return new JsonObject { ["topic"] = name, ["guide"] = document }.ToJsonString();
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
            ["writes_allowed"] = graft.AllowWrites,
            ["delete_and_transfer_allowed"] = graft.AllowDestructive
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

    [McpServerTool(Name = "graft_list_page_artifacts")]
    [Description("Lists a project's page artifacts: named folders of uploaded files, such as a browser build, that are not in the repository. Each is private until promoted; a promoted one is served as part of the project's site, at the url given, while the project's page is published. Shows each file and the content type it would be served as, or null when the server does not serve that type.")]
    public static async Task<string> ListPageArtifacts(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (PageArtifactListResponse? list, string? error) = await graft.GetAsync(
            "api/graft/projects/" + Encode(project) + "/page-artifacts", ApiJson.Default.PageArtifactListResponse, cancellationToken);
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["project"] = project,
            ["page_published"] = list!.PagePublished,
            ["pages_origin_configured"] = list.PagesAvailable,
            ["served_under"] = list.Prefix + "/<name>/",
            ["largest_upload_bytes"] = list.MaximumBytes,
            ["artifacts"] = Array(list.Artifacts, PageArtifactJson)
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_upload_page_artifact")]
    [Description("Uploads a page artifact from a zip archive you already have as base64, replacing any artifact of the same name as a whole. A new artifact starts private; one that is already promoted stays promoted, so its new files are public at once. A single folder wrapping everything in the archive is dropped. An archive too large to pass as base64 (roughly 10 MB) is sent instead with an HTTP PUT of the zip to /api/graft/projects/<project>/page-artifacts/<name>, with a token. Needs the Developer role. Refused unless writes are allowed on this server.")]
    public static async Task<string> UploadPageArtifact(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The artifact's name: up to 40 lower-case letters, digits, '-' and '_'. It becomes a folder in the site's address.")] string name,
        [Description("The zip archive's bytes, base64 encoded.")] string zipBase64,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "uploading a page artifact");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(zipBase64 ?? ""); }
        catch (FormatException) { return Failure("The archive must be base64 encoded."); }
        if (bytes.Length == 0) return Failure("The archive is empty.");

        (bool ok, string response, int status) = await graft.SendBytesAsync(HttpMethod.Put,
            "api/graft/projects/" + Encode(project) + "/page-artifacts/" + Uri.EscapeDataString((name ?? "").Trim()),
            bytes, "application/zip", cancellationToken);
        if (!ok) return Failure(Explain(status, response));
        return Created(response, ApiJson.Default.PageArtifactResponse, PageArtifactJson);
    }

    [McpServerTool(Name = "graft_set_page_artifact_promoted")]
    [Description("Promotes a page artifact, which makes its files public on the pages origin as part of the project's site, or withdraws it again. A promoted artifact is only reachable while the project's page is published. Needs the Maintainer role. Refused unless writes are allowed on this server.")]
    public static async Task<string> SetPageArtifactPromoted(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The artifact's name.")] string name,
        [Description("True to promote the artifact, false to withdraw it.")] bool promoted,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "promoting or withdrawing a page artifact");
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/graft/projects/" + Encode(project) + "/page-artifacts/" + Uri.EscapeDataString((name ?? "").Trim()) + "/promotion",
            new JsonObject { ["promoted"] = promoted }, cancellationToken);
        if (!ok) return Failure(Explain(status, response));
        return Created(response, ApiJson.Default.PageArtifactResponse, PageArtifactJson);
    }

    [McpServerTool(Name = "graft_delete_page_artifact")]
    [Description("Deletes a page artifact and its files. If it was promoted, its part of the project's site stops answering at once. This cannot be undone. Needs the Maintainer role. Refused unless deleting is allowed on this server.")]
    public static async Task<string> DeletePageArtifact(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The artifact's name.")] string name,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowDestructive) return RefusedDestructive(graft, "deleting a page artifact");
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Delete,
            "api/graft/projects/" + Encode(project) + "/page-artifacts/" + Uri.EscapeDataString((name ?? "").Trim()), null, cancellationToken);
        return ok
            ? new JsonObject { ["project"] = project, ["name"] = name, ["deleted"] = true }.ToJsonString()
            : Failure(Explain(status, response));
    }

    private static JsonObject PageArtifactJson(PageArtifactResponse artifact) => new()
    {
        ["name"] = artifact.Name,
        ["promoted"] = artifact.Promoted,
        ["url"] = artifact.Url,
        ["files"] = artifact.FileCount,
        ["bytes"] = artifact.Bytes,
        ["updated_at"] = artifact.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
        ["updated_by"] = artifact.UpdatedBy,
        ["contents"] = Array(artifact.Files, file => new JsonObject { ["path"] = file.Path, ["size"] = file.Size, ["served_as"] = file.ContentType }),
        ["contents_truncated"] = artifact.FilesTruncated
    };

    [McpServerTool(Name = "graft_list_issues")]
    [Description("Lists a project's issues, newest first. State may be opened, closed, or all.")]
    public static async Task<string> ListIssues(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("One of opened, closed or all. Empty means all.")] string state,
        CancellationToken cancellationToken)
    {
        if (!TryState(state, merged: false, out string? query)) return Failure("state must be opened, closed or all.");
        (IssueResponse[]? issues, long total, _, string? error) = await graft.GetListAsync(
            "api/v4/projects/" + Encode(project) + "/issues" + query, ApiJson.Default.IssueResponseArray, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["total"] = total,
            ["issues"] = Array(issues!, issue => new JsonObject
            {
                ["number"] = issue.Iid,
                ["title"] = issue.Title,
                ["state"] = issue.State,
                ["author"] = issue.Author.Username,
                ["comments"] = issue.UserNotesCount,
                ["labels"] = Texts(issue.Labels ?? []),
                ["milestone"] = issue.Milestone?.Title,
                ["assignees"] = Texts([.. (issue.Assignees ?? []).Select(person => person.Username)]),
                ["due_date"] = issue.DueDate,
                ["confidential"] = issue.Confidential,
                ["updated_at"] = issue.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
                ["web_url"] = issue.WebUrl
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_read_issue")]
    [Description("Reads one issue by its number, with its description and every comment on it.")]
    public static async Task<string> ReadIssue(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The issue number as shown in the project, counted from one.")] long number,
        CancellationToken cancellationToken)
    {
        string prefix = "api/v4/projects/" + Encode(project) + "/issues/" + Number(number);
        (IssueResponse? issue, string? error) = await graft.GetAsync(prefix, ApiJson.Default.IssueResponse, cancellationToken);
        if (error is not null) return Failure(error);
        (NoteResponse[]? notes, string? notesError) =
            await graft.GetAsync(prefix + "/notes", ApiJson.Default.NoteResponseArray, cancellationToken);

        return new JsonObject
        {
            ["number"] = issue!.Iid,
            ["title"] = issue.Title,
            ["state"] = issue.State,
            ["author"] = issue.Author.Username,
            ["created_at"] = issue.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["description"] = issue.Description,
            ["labels"] = Texts(issue.Labels ?? []),
            ["milestone"] = issue.Milestone?.Title,
            ["assignees"] = Texts([.. (issue.Assignees ?? []).Select(person => person.Username)]),
            ["due_date"] = issue.DueDate,
            ["confidential"] = issue.Confidential,
            ["web_url"] = issue.WebUrl,
            ["comments"] = notesError is not null || notes is null
                ? null
                : Array(notes, note => new JsonObject
                {
                    ["id"] = note.Id,
                    ["author"] = note.Author.Username,
                    ["created_at"] = note.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["body"] = note.Body,
                    ["reply_to"] = note.ParentId,
                    ["resolved"] = note.ParentId is null ? note.Resolved : null,
                    ["edited"] = note.EditedAt is not null
                })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_open_issue")]
    [Description("Opens an issue on a project. Needs membership: reading a public project is not enough to write on it. Refused unless writes are allowed on this server.")]
    public static async Task<string> OpenIssue(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("One line saying what the issue is.")] string title,
        [Description("The detail. May be empty. Markdown is shown as written; it is not rendered here.")] string description,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "opening an issue");
        if (string.IsNullOrWhiteSpace(title)) return Failure("title is required.");

        JsonObject body = new() { ["title"] = title.Trim() };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/issues", body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        return Created(response, ApiJson.Default.IssueResponse, issue => new JsonObject
        {
            ["opened"] = true,
            ["number"] = issue.Iid,
            ["web_url"] = issue.WebUrl
        });
    }

    [McpServerTool(Name = "graft_close_issue")]
    [Description("Closes or reopens an issue. The author or a Maintainer may do either. Refused unless writes are allowed on this server.")]
    public static async Task<string> CloseIssue(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The issue number as shown in the project.")] long number,
        [Description("Either close or reopen.")] string action,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "closing or reopening an issue");
        if (action is not ("close" or "reopen")) return Failure("action must be close or reopen.");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/v4/projects/" + Encode(project) + "/issues/" + Number(number),
            new JsonObject { ["state_event"] = action }, cancellationToken);
        return ok
            ? new JsonObject { ["number"] = number, ["state"] = action == "close" ? "closed" : "opened" }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
    }

    [McpServerTool(Name = "graft_comment")]
    [Description("Adds a comment to an issue or a merge request. Give file, line and side together to put the comment on one line of a merge request's diff, or leave all three empty to comment on the whole thing. Needs membership. Refused unless writes are allowed on this server.")]
    public static async Task<string> Comment(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("Either issue or merge_request.")] string subject,
        [Description("The number of the issue or merge request, as shown in the project.")] long number,
        [Description("What to say.")] string body,
        [Description("Optional: the file to comment on, as graft_read_merge_request reports its path. Empty comments on the whole thing.")] string file,
        [Description("Optional: the line number within that file. Zero when commenting on the whole thing.")] long line,
        [Description("Optional: old for a removed line, new for an added or unchanged one. Empty when commenting on the whole thing.")] string side,
        [Description("Optional: the id of a comment to answer, as graft_read_issue and graft_read_merge_request report it. Zero starts a new thread. A reply joins that comment's thread and cannot name a line of its own.")] long reply_to,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "commenting");
        if (subject is not ("issue" or "merge_request")) return Failure("subject must be issue or merge_request.");
        if (string.IsNullOrWhiteSpace(body)) return Failure("body is required.");

        bool anchored = !string.IsNullOrWhiteSpace(file) || line > 0 || !string.IsNullOrWhiteSpace(side);
        if (anchored)
        {
            if (string.IsNullOrWhiteSpace(file) || line <= 0 || side is not ("old" or "new"))
                return Failure("A comment on a line needs file, a line above zero, and side of old or new, together.");
            if (subject != "merge_request") return Failure("Only a merge request has a diff to comment on.");
            if (reply_to > 0) return Failure("A reply follows its thread; give reply_to or a line, not both.");
        }

        JsonObject payload = new() { ["body"] = body };
        if (anchored)
        {
            payload["path"] = file.Trim();
            payload["line"] = line;
            payload["side"] = side;
        }
        if (reply_to > 0) payload["parent_id"] = reply_to;
        string path = "api/v4/projects/" + Encode(project) +
            (subject == "issue" ? "/issues/" : "/merge_requests/") + Number(number) + "/notes";
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post, path, payload, cancellationToken);
        return ok
            ? new JsonObject
            {
                ["commented"] = true,
                ["subject"] = subject,
                ["number"] = number,
                ["file"] = anchored ? file.Trim() : null,
                ["line"] = anchored ? line : null,
                ["reply_to"] = reply_to > 0 ? reply_to : null
            }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
    }

    [McpServerTool(Name = "graft_create_branch")]
    [Description("Creates a branch pointing at an existing commit, branch or tag. Writes no content; use graft_write_file to put something on it. Refused unless writes are allowed on this server.")]
    public static async Task<string> CreateBranch(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The name for the new branch.")] string branch,
        [Description("What it should start from: a branch, a tag or a commit id. Empty uses the project's default branch.")] string from,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "creating a branch");
        if (string.IsNullOrWhiteSpace(branch)) return Failure("branch is required.");

        JsonObject body = new() { ["branch"] = branch.Trim() };
        if (!string.IsNullOrWhiteSpace(from)) body["ref"] = from.Trim();
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/repository/branches", body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        return Created(response, ApiJson.Default.BranchResponse, created => new JsonObject
        {
            ["created"] = true,
            ["branch"] = created.Name,
            ["commit"] = created.Commit.Id
        });
    }

    [McpServerTool(Name = "graft_list_merge_requests")]
    [Description("Lists a project's merge requests. State may be opened, closed, merged, or all. Whether each one can be merged is not reported here; read one to find out.")]
    public static async Task<string> ListMergeRequests(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("One of opened, closed, merged or all. Empty means all.")] string state,
        CancellationToken cancellationToken)
    {
        if (!TryState(state, merged: true, out string? query))
            return Failure("state must be opened, closed, merged or all.");
        (MergeRequestResponse[]? proposals, long total, _, string? error) = await graft.GetListAsync(
            "api/v4/projects/" + Encode(project) + "/merge_requests" + query,
            ApiJson.Default.MergeRequestResponseArray, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["total"] = total,
            ["merge_requests"] = Array(proposals!, request => new JsonObject
            {
                ["number"] = request.Iid,
                ["title"] = request.Title,
                ["state"] = request.State,
                ["source_branch"] = request.SourceBranch,
                ["target_branch"] = request.TargetBranch,
                ["author"] = request.Author.Username,
                ["comments"] = request.UserNotesCount,
                ["web_url"] = request.WebUrl
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_read_merge_request")]
    [Description("Reads one merge request: its description, whether it can be merged and how, any conflicting paths, the files it changes, and every comment on it.")]
    public static async Task<string> ReadMergeRequest(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The merge request number as shown in the project.")] long number,
        CancellationToken cancellationToken)
    {
        string prefix = "api/v4/projects/" + Encode(project) + "/merge_requests/" + Number(number);
        (MergeRequestChangesResponse? changes, string? error) =
            await graft.GetAsync(prefix + "/changes", ApiJson.Default.MergeRequestChangesResponse, cancellationToken);
        if (error is not null) return Failure(error);
        (NoteResponse[]? notes, string? notesError) =
            await graft.GetAsync(prefix + "/notes", ApiJson.Default.NoteResponseArray, cancellationToken);

        MergeRequestResponse request = changes!.MergeRequest;
        return new JsonObject
        {
            ["number"] = request.Iid,
            ["title"] = request.Title,
            ["state"] = request.State,
            ["author"] = request.Author.Username,
            ["description"] = request.Description,
            ["source_branch"] = request.SourceBranch,
            ["target_branch"] = request.TargetBranch,
            ["can_be_merged"] = request.MergeStatus == "can_be_merged",
            ["draft"] = request.Draft,
            ["labels"] = Texts(request.Labels ?? []),
            ["milestone"] = request.Milestone?.Title,
            ["assignees"] = Texts([.. (request.Assignees ?? []).Select(person => person.Username)]),
            ["reviewers"] = Texts([.. (request.Reviewers ?? []).Select(person => person.Username)]),
            ["merge_method"] = request.MergeMethod,
            ["conflicts"] = request.Conflicts is null ? null : Texts(request.Conflicts),
            ["changed_files"] = Array(changes.Files, file => new JsonObject
            {
                ["path"] = file.Path,
                ["status"] = file.Status,
                ["added"] = file.Added,
                ["removed"] = file.Removed
            }),
            ["web_url"] = request.WebUrl,
            ["comments"] = notesError is not null || notes is null
                ? null
                : Array(notes, note => new JsonObject
                {
                    ["id"] = note.Id,
                    ["author"] = note.Author.Username,
                    ["body"] = note.Body,
                    ["file"] = note.Path,
                    ["line"] = note.Line,
                    ["outdated"] = note.Path is null ? null : note.Outdated,
                    ["reply_to"] = note.ParentId,
                    ["resolved"] = note.ParentId is null ? note.Resolved : null,
                    ["edited"] = note.EditedAt is not null
                })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_open_merge_request")]
    [Description("Proposes merging one existing branch into another. Both branches must already exist; this does not create or push a branch. Refused unless writes are allowed on this server.")]
    public static async Task<string> OpenMergeRequest(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The branch holding the work.")] string sourceBranch,
        [Description("The branch it should go into.")] string targetBranch,
        [Description("One line saying what the change does.")] string title,
        [Description("The detail. May be empty.")] string description,
        [Description("Optional: the fork the source branch lives in, as a path, slug or id, when proposing to a project you cannot push to. Empty means the branch is in the project itself.")] string sourceProject,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "proposing a merge");
        if (string.IsNullOrWhiteSpace(sourceBranch) || string.IsNullOrWhiteSpace(targetBranch))
            return Failure("source and target branches are required.");
        if (string.IsNullOrWhiteSpace(title)) return Failure("title is required.");

        JsonObject body = new()
        {
            ["source_branch"] = sourceBranch.Trim(),
            ["target_branch"] = targetBranch.Trim(),
            ["title"] = title.Trim()
        };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;
        if (!string.IsNullOrWhiteSpace(sourceProject))
        {
            (ProjectResponse? fork, string? forkError) = await graft.GetAsync("api/v4/projects/" + Encode(sourceProject), ApiJson.Default.ProjectResponse, cancellationToken);
            if (forkError is not null) return Failure("sourceProject: " + forkError);
            body["source_project_id"] = fork!.Id;
        }
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/merge_requests", body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        return Created(response, ApiJson.Default.MergeRequestResponse, request => new JsonObject
        {
            ["opened"] = true,
            ["number"] = request.Iid,
            ["merge_method"] = request.MergeMethod,
            ["web_url"] = request.WebUrl
        });
    }

    [McpServerTool(Name = "graft_merge")]
    [Description("Merges an open merge request into its target branch. Needs the same permission as pushing. Fast-forwards where it can, otherwise writes a merge commit; refuses when both branches changed the same file, and names those files. Refused unless writes are allowed on this server.")]
    public static async Task<string> Merge(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The merge request number as shown in the project.")] long number,
        [Description("Optional: true to squash the branch into one commit on the target, with the merge request's title as its message. The branch's own commits are left behind.")] bool squash,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "merging");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/v4/projects/" + Encode(project) + "/merge_requests/" + Number(number) + "/merge",
            squash ? new JsonObject { ["squash"] = true } : null, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        return Created(response, ApiJson.Default.MergeResultResponse, result => new JsonObject
        {
            ["merged"] = true,
            ["method"] = result.Method,
            ["commit"] = result.MergeCommitSha,
            ["target_branch"] = result.MergeRequest.TargetBranch
        });
    }

    [McpServerTool(Name = "graft_list_labels")]
    [Description("Lists a project's labels: the words it sorts its issues and merge requests with. Every label an issue or merge request may carry has to be one of these.")]
    public static async Task<string> ListLabels(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (LabelResponse[]? labels, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project) + "/labels", ApiJson.Default.LabelResponseArray, cancellationToken);
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["labels"] = Array(labels!, label => new JsonObject
            {
                ["name"] = label.Name,
                ["color"] = label.Color,
                ["description"] = label.Description
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_list_milestones")]
    [Description("Lists a project's milestones with how many issues each has open and closed. State may be active, closed or all.")]
    public static async Task<string> ListMilestones(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("One of active, closed or all. Empty means active.")] string state,
        CancellationToken cancellationToken)
    {
        string chosen = (state ?? "").Trim();
        if (chosen is not ("" or "active" or "closed" or "all")) return Failure("state must be active, closed or all.");
        (MilestoneResponse[]? milestones, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project) + "/milestones" + (chosen.Length == 0 ? "" : "?state=" + chosen),
            ApiJson.Default.MilestoneResponseArray, cancellationToken);
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["milestones"] = Array(milestones!, milestone => new JsonObject
            {
                ["id"] = milestone.Id,
                ["number"] = milestone.Iid,
                ["title"] = milestone.Title,
                ["state"] = milestone.State,
                ["due_date"] = milestone.DueDate,
                ["open_issues"] = milestone.OpenIssues,
                ["closed_issues"] = milestone.ClosedIssues,
                ["web_url"] = milestone.WebUrl
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_list_releases")]
    [Description("Lists a project's releases, newest first: the tag each sits on, its notes, the files attached and the links it carries, with download addresses.")]
    public static async Task<string> ListReleases(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        (ReleaseResponse[]? releases, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project) + "/releases", ApiJson.Default.ReleaseResponseArray, cancellationToken);
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["releases"] = Array(releases!, release => new JsonObject
            {
                ["tag"] = release.TagName,
                ["name"] = release.Name,
                ["released_at"] = release.ReleasedAt.ToString("O", CultureInfo.InvariantCulture),
                ["author"] = release.Author.Username,
                ["tag_exists"] = release.TagExists,
                ["commit"] = release.Commit?.Id,
                ["notes"] = release.Description,
                ["files"] = Array(release.Assets.Files, file => new JsonObject
                {
                    ["name"] = file.FileName,
                    ["size"] = file.Size,
                    ["sha256"] = file.Sha256,
                    ["url"] = file.Url
                }),
                ["links"] = Array(release.Assets.Links, link => new JsonObject
                {
                    ["name"] = link.Name,
                    ["url"] = link.Url,
                    ["kind"] = link.LinkType
                }),
                ["web_url"] = release.WebUrl
            })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_read_wiki")]
    [Description("Reads a project's wiki: with no page, lists the pages; with a page's slug, returns that page's markdown source and its history.")]
    public static async Task<string> ReadWiki(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("Optional: the page's slug, as the listing reports it. Empty lists the pages.")] string page,
        CancellationToken cancellationToken)
    {
        string prefix = "api/v4/projects/" + Encode(project) + "/wikis";
        if (string.IsNullOrWhiteSpace(page))
        {
            (WikiPageResponse[]? pages, string? listError) = await graft.GetAsync(prefix, ApiJson.Default.WikiPageResponseArray, cancellationToken);
            if (listError is not null) return Failure(listError);
            return new JsonObject
            {
                ["pages"] = Array(pages!, entry => new JsonObject
                {
                    ["slug"] = entry.Slug,
                    ["title"] = entry.Title,
                    ["updated_by"] = entry.UpdatedBy,
                    ["updated_at"] = entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["web_url"] = entry.WebUrl
                })
            }.ToJsonString();
        }
        (WikiPageResponse? found, string? error) = await graft.GetAsync(prefix + "/" + Uri.EscapeDataString(page.Trim()), ApiJson.Default.WikiPageResponse, cancellationToken);
        if (error is not null) return Failure(error);
        (WikiVersionResponse[]? versions, _) = await graft.GetAsync(prefix + "/" + Uri.EscapeDataString(page.Trim()) + "/versions", ApiJson.Default.WikiVersionResponseArray, cancellationToken);
        return new JsonObject
        {
            ["slug"] = found!.Slug,
            ["title"] = found.Title,
            ["content"] = found.Content,
            ["updated_by"] = found.UpdatedBy,
            ["updated_at"] = found.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["versions"] = versions is null ? null : Array(versions, version => new JsonObject
            {
                ["id"] = version.Id,
                ["message"] = version.Message,
                ["author"] = version.Author,
                ["created_at"] = version.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
            }),
            ["web_url"] = found.WebUrl
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_write_wiki")]
    [Description("Creates a wiki page, or replaces an existing page's content. Markdown. The previous text is kept as a version, so nothing is lost; read the page first unless you mean to replace it. Needs the permission to push. Refused unless writes are allowed on this server.")]
    public static async Task<string> WriteWiki(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("The page's title. For an existing page, pass its slug as page and this may be empty to keep the title.")] string title,
        [Description("Optional: the slug of an existing page to update. Empty creates a new page from the title.")] string page,
        [Description("The whole markdown content of the page.")] string content,
        [Description("Optional: a word about the change, kept with the version.")] string message,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "writing the wiki");
        if (content is null) return Failure("content is required.");
        JsonObject body = new() { ["content"] = content };
        if (!string.IsNullOrWhiteSpace(title)) body["title"] = title.Trim();
        if (!string.IsNullOrWhiteSpace(message)) body["message"] = message.Trim();
        bool creating = string.IsNullOrWhiteSpace(page);
        if (creating && string.IsNullOrWhiteSpace(title)) return Failure("title is required for a new page.");
        string path = "api/v4/projects/" + Encode(project) + "/wikis" + (creating ? "" : "/" + Uri.EscapeDataString(page.Trim()));
        (bool ok, string response, int status) = await graft.SendAsync(creating ? HttpMethod.Post : HttpMethod.Put, path, body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        return Created(response, ApiJson.Default.WikiPageResponse, saved => new JsonObject
        {
            ["saved"] = true,
            ["slug"] = saved.Slug,
            ["title"] = saved.Title,
            ["versions"] = saved.Versions,
            ["web_url"] = saved.WebUrl
        });
    }

    [McpServerTool(Name = "graft_search")]
    [Description("Searches. With a project: its code (scope blobs), commits, issues, merge requests or wiki. Without one: the server's projects, issues or merge requests, across whatever the token can see. A match is a case-insensitive substring; results are bounded and say when they were cut short.")]
    public static async Task<string> Search(
        GraftClient graft,
        [Description("Optional: the project path like alice/widgets, its instance-wide slug, or its numeric id. Empty searches across the server.")] string project,
        [Description("With a project: issues, merge_requests, blobs, wiki_blobs or commits. Without one: projects, issues or merge_requests.")] string scope,
        [Description("What to look for, at most 200 characters.")] string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return Failure("query is required.");
        string path = (string.IsNullOrWhiteSpace(project) ? "api/v4/search" : "api/v4/projects/" + Encode(project) + "/search") +
            "?scope=" + Uri.EscapeDataString((scope ?? "").Trim()) + "&search=" + Uri.EscapeDataString(query.Trim());
        // GitLab's search answers a bare array of the scope asked for, so the scope decides what to read.
        string wanted = (scope ?? "").Trim();
        ProjectResponse[] projects = []; IssueResponse[] issues = []; MergeRequestResponse[] proposals = [];
        BlobSearchResponse[] blobs = []; WikiPageResponse[] wiki = []; CommitResponse[] commits = [];
        long count; bool truncated; string? error;
        switch (wanted)
        {
            case "projects": (ProjectResponse[]? p, count, truncated, error) = await graft.GetListAsync(path, ApiJson.Default.ProjectResponseArray, cancellationToken); projects = p ?? []; break;
            case "issues": (IssueResponse[]? i, count, truncated, error) = await graft.GetListAsync(path, ApiJson.Default.IssueResponseArray, cancellationToken); issues = i ?? []; break;
            case "merge_requests": (MergeRequestResponse[]? m, count, truncated, error) = await graft.GetListAsync(path, ApiJson.Default.MergeRequestResponseArray, cancellationToken); proposals = m ?? []; break;
            case "blobs": (BlobSearchResponse[]? b, count, truncated, error) = await graft.GetListAsync(path, ApiJson.Default.BlobSearchResponseArray, cancellationToken); blobs = b ?? []; break;
            case "wiki_blobs": (WikiPageResponse[]? w, count, truncated, error) = await graft.GetListAsync(path, ApiJson.Default.WikiPageResponseArray, cancellationToken); wiki = w ?? []; break;
            default: (CommitResponse[]? c, count, truncated, error) = await graft.GetListAsync(path, ApiJson.Default.CommitResponseArray, cancellationToken); commits = c ?? []; break;
        }
        if (error is not null) return Failure(error);
        return new JsonObject
        {
            ["scope"] = wanted,
            ["count"] = count,
            ["truncated"] = truncated,
            ["projects"] = Array(projects, Summarise),
            ["issues"] = Array(issues, issue => new JsonObject { ["number"] = issue.Iid, ["title"] = issue.Title, ["state"] = issue.State, ["web_url"] = issue.WebUrl }),
            ["merge_requests"] = Array(proposals, request => new JsonObject { ["number"] = request.Iid, ["title"] = request.Title, ["state"] = request.State, ["web_url"] = request.WebUrl }),
            ["blobs"] = Array(blobs, blob => new JsonObject { ["path"] = blob.Path, ["line"] = blob.Startline, ["text"] = blob.Data }),
            ["wiki"] = Array(wiki, page => new JsonObject { ["slug"] = page.Slug, ["title"] = page.Title }),
            ["commits"] = Array(commits, commit => new JsonObject { ["id"] = commit.Id, ["title"] = commit.Title, ["author"] = commit.AuthorName })
        }.ToJsonString();
    }

    [McpServerTool(Name = "graft_fork")]
    [Description("Forks a project into the token owner's own namespace: a copy with the original's content, to push to and propose from when the original cannot be written to. Needs only to be able to read the original. Refused unless writes are allowed on this server.")]
    public static async Task<string> Fork(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "forking");
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post,
            "api/v4/projects/" + Encode(project) + "/fork", new JsonObject(), cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        return Created(response, ApiJson.Default.ProjectResponse, fork => new JsonObject
        {
            ["forked"] = true,
            ["path"] = fork.PathWithNamespace,
            ["id"] = fork.Id,
            ["clone_url"] = fork.HttpUrlToRepo,
            ["forked_from"] = fork.ForkedFromProject?.PathWithNamespace,
            ["hint"] = "Push a branch to the fork, then graft_open_merge_request on the original with source_project_id set to this fork's id."
        });
    }

    [McpServerTool(Name = "graft_triage")]
    [Description("Sets the labels, milestone, assignees or due date of an issue or a merge request. Each argument left empty is left alone. Needs to be the author or able to push. Labels must already exist in the project; graft_list_labels shows them. Refused unless writes are allowed on this server.")]
    public static async Task<string> Triage(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("Either issue or merge_request.")] string subject,
        [Description("The number of the issue or merge request, as shown in the project.")] long number,
        [Description("Optional: the labels to add, comma-separated. Empty adds none.")] string add_labels,
        [Description("Optional: the labels to remove, comma-separated. Empty removes none.")] string remove_labels,
        [Description("Optional: a milestone id from graft_list_milestones, or -1 to clear the milestone. Zero leaves it alone.")] long milestone_id,
        [Description("Optional: the usernames to assign, comma-separated, replacing the current assignees. Each must be a member of the project. Empty leaves them alone; the word none clears them.")] string assignees,
        [Description("Optional, issues only: a due date as YYYY-MM-DD, or the word none to clear it. Empty leaves it alone.")] string due_date,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "triaging");
        if (subject is not ("issue" or "merge_request")) return Failure("subject must be issue or merge_request.");
        if (subject == "merge_request" && !string.IsNullOrWhiteSpace(due_date)) return Failure("A merge request has no due date.");

        JsonObject body = new();
        if (!string.IsNullOrWhiteSpace(add_labels)) body["add_labels"] = add_labels.Trim();
        if (!string.IsNullOrWhiteSpace(remove_labels)) body["remove_labels"] = remove_labels.Trim();
        if (milestone_id != 0) body["milestone_id"] = milestone_id < 0 ? 0 : milestone_id;
        if (!string.IsNullOrWhiteSpace(assignees))
        {
            JsonArray names = [];
            if (!string.Equals(assignees.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                foreach (string name in assignees.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    names.Add((JsonNode?)JsonValue.Create(name));
            body["assignee_usernames"] = names;
        }
        if (!string.IsNullOrWhiteSpace(due_date))
            body["due_date"] = string.Equals(due_date.Trim(), "none", StringComparison.OrdinalIgnoreCase) ? "" : due_date.Trim();
        if (body.Count == 0) return Failure("Nothing to change: give at least one of the optional arguments.");

        string path = "api/v4/projects/" + Encode(project) + (subject == "issue" ? "/issues/" : "/merge_requests/") + Number(number);
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put, path, body, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        return subject == "issue"
            ? Created(response, ApiJson.Default.IssueResponse, issue => new JsonObject
            {
                ["number"] = issue.Iid,
                ["labels"] = Texts(issue.Labels ?? []),
                ["milestone"] = issue.Milestone?.Title,
                ["assignees"] = Texts([.. (issue.Assignees ?? []).Select(person => person.Username)]),
                ["due_date"] = issue.DueDate
            })
            : Created(response, ApiJson.Default.MergeRequestResponse, request => new JsonObject
            {
                ["number"] = request.Iid,
                ["labels"] = Texts(request.Labels ?? []),
                ["milestone"] = request.Milestone?.Title,
                ["assignees"] = Texts([.. (request.Assignees ?? []).Select(person => person.Username)])
            });
    }

    /// <summary>
    /// Re-reads a successful write's own response so the tool can report what was created. A failure to
    /// parse it is reported as success without the detail rather than as an error: the write happened, and
    /// telling an agent it failed would invite it to do the thing twice.
    /// </summary>
    private static string Created<T>(string response, JsonTypeInfo<T> type, Func<T, JsonObject> describe)
    {
        try
        {
            T? value = JsonSerializer.Deserialize(response, type);
            if (value is not null) return describe(value).ToJsonString();
        }
        catch (JsonException) { }
        return new JsonObject { ["done"] = true }.ToJsonString();
    }

    private static bool TryState(string? value, bool merged, out string? query)
    {
        query = null;
        string state = (value ?? "").Trim();
        if (state.Length == 0 || state == "all") { query = ""; return true; }
        if (state is not ("opened" or "closed") && !(merged && state == "merged")) return false;
        query = "?state=" + state;
        return true;
    }

    private static JsonArray Texts(IReadOnlyList<string> values)
    {
        JsonArray array = [];
        foreach (string value in values) array.Add((JsonNode)JsonValue.Create(value));
        return array;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

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

    private static string RefusedDestructive(GraftClient graft, string action) => new JsonObject
    {
        ["error"] = true,
        ["refused"] = graft.AllowWrites
            ? "This Graft MCP server does not let agents delete or transfer projects, so " + action + " is not available."
            : "This Graft MCP server is read-only, so " + action + " is not available.",
        ["remedy"] = graft.AllowWrites ? graft.DestructiveRemedy : graft.WriteRemedy
    }.ToJsonString();

    private static string Failure(string message) =>
        new JsonObject { ["error"] = true, ["message"] = message }.ToJsonString();

    /// <summary><see cref="GraftClient.Describe"/>, except that a refusal carrying the server's own reason passes it
    /// on. Settings routes refuse for reasons other than a token's scope (only the owner may transfer, a tag already
    /// exists), and the generic sentence would send an agent after the wrong fix.</summary>
    private static string Explain(int status, string body)
    {
        if (status is 400 or 403 or 409 or 413 && Field(body, "message") is string reason && reason.Length > 0)
            return "Graft refused: " + reason;
        return GraftClient.Describe(status, body);
    }

    /// <summary>A webhook route that is absent because the operator never turned webhooks on answers 404 exactly
    /// like a project the caller may not see, so both are named.</summary>
    private static string ExplainHooks(int status, string body) => status == 404
        ? "Not found: either no such project or one the token's owner may not see, or this server's operator has not turned webhooks on (--webhooks true)."
        : Explain(status, body);

    /// <summary>One top-level string field of a JSON reply, or null.</summary>
    private static string? Field(string body, string name)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject root && root[name] is JsonValue value && value.TryGetValue(out string? text)
                ? text
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static T? Read<T>(string body, JsonTypeInfo<T> type)
    {
        try { return JsonSerializer.Deserialize(body, type); }
        catch (JsonException) { return default; }
    }

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
