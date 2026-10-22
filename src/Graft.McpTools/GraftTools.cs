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
        string path = "api/v4/projects/" + Encode(project) + "/repository/commits/" +
            Uri.EscapeDataString((commit ?? "").Trim()) + "/diff" + (parent == 0 ? "" : "?parent=" + parent.ToString(CultureInfo.InvariantCulture));
        (CommitDiffResponse? diff, string? error) = await graft.GetAsync(path, ApiJson.Default.CommitDiffResponse, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["commit"] = diff!.Commit.Id,
            ["title"] = diff.Commit.Title,
            ["author"] = diff.Commit.AuthorName,
            ["compared_against"] = diff.ParentId,
            ["added"] = diff.Added,
            ["removed"] = diff.Removed,
            // Said out loud: a truncated diff that looked complete would have an agent conclude a file was
            // untouched when the server simply stopped listing.
            ["truncated"] = diff.Truncated,
            ["files"] = Array(diff.Files, file => new JsonObject
            {
                ["path"] = file.Path,
                ["status"] = file.Status,
                ["added"] = file.Added,
                ["removed"] = file.Removed,
                ["binary"] = file.IsBinary,
                ["too_large"] = file.TooLarge,
                ["diff"] = file.IsBinary || file.TooLarge ? null : Unified(file)
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

    [McpServerTool(Name = "graft_list_issues")]
    [Description("Lists a project's issues, newest first. State may be opened, closed, or all.")]
    public static async Task<string> ListIssues(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("One of opened, closed or all. Empty means all.")] string state,
        CancellationToken cancellationToken)
    {
        if (!TryState(state, merged: false, out string? query)) return Failure("state must be opened, closed or all.");
        (IssuePageResponse? page, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project) + "/issues" + query, ApiJson.Default.IssuePageResponse, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["total"] = page!.Total,
            ["issues"] = Array(page.Issues, issue => new JsonObject
            {
                ["number"] = issue.Iid,
                ["title"] = issue.Title,
                ["state"] = issue.State,
                ["author"] = issue.Author.Username,
                ["comments"] = issue.UserNotesCount,
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
            ["web_url"] = issue.WebUrl,
            ["comments"] = notesError is not null || notes is null
                ? null
                : Array(notes, note => new JsonObject
                {
                    ["author"] = note.Author.Username,
                    ["created_at"] = note.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["body"] = note.Body
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
    [Description("Adds a comment to an issue or a merge request. Needs membership. Refused unless writes are allowed on this server.")]
    public static async Task<string> Comment(
        GraftClient graft,
        [Description("The project path like alice/widgets, its instance-wide slug, or its numeric id.")] string project,
        [Description("Either issue or merge_request.")] string subject,
        [Description("The number of the issue or merge request, as shown in the project.")] long number,
        [Description("What to say.")] string body,
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "commenting");
        if (subject is not ("issue" or "merge_request")) return Failure("subject must be issue or merge_request.");
        if (string.IsNullOrWhiteSpace(body)) return Failure("body is required.");

        string path = "api/v4/projects/" + Encode(project) +
            (subject == "issue" ? "/issues/" : "/merge_requests/") + Number(number) + "/notes";
        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Post, path,
            new JsonObject { ["body"] = body }, cancellationToken);
        return ok
            ? new JsonObject { ["commented"] = true, ["subject"] = subject, ["number"] = number }.ToJsonString()
            : Failure(GraftClient.Describe(status, response));
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
        (MergeRequestPageResponse? page, string? error) = await graft.GetAsync(
            "api/v4/projects/" + Encode(project) + "/merge_requests" + query,
            ApiJson.Default.MergeRequestPageResponse, cancellationToken);
        if (error is not null) return Failure(error);

        return new JsonObject
        {
            ["total"] = page!.Total,
            ["merge_requests"] = Array(page.MergeRequests, request => new JsonObject
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
                    ["author"] = note.Author.Username,
                    ["body"] = note.Body
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
        CancellationToken cancellationToken)
    {
        if (!graft.AllowWrites) return Refused(graft, "merging");

        (bool ok, string response, int status) = await graft.SendAsync(HttpMethod.Put,
            "api/v4/projects/" + Encode(project) + "/merge_requests/" + Number(number) + "/merge", null, cancellationToken);
        if (!ok) return Failure(GraftClient.Describe(status, response));
        return Created(response, ApiJson.Default.MergeResultResponse, result => new JsonObject
        {
            ["merged"] = true,
            ["method"] = result.Method,
            ["commit"] = result.MergeCommitSha,
            ["target_branch"] = result.MergeRequest.TargetBranch
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
