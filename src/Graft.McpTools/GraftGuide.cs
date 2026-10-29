namespace Graft.Mcp;

/// <summary>
/// What an agent needs to know about this forge, in one place.
///
/// <para>
/// Three surfaces read from here and none of them keeps its own copy: the Model Context Protocol
/// handshake, which carries <see cref="Instructions"/> to every client before a single tool is called; the
/// <c>graft_help</c> tool, which serves the longer <see cref="Topics"/> on demand; and <c>/llms.txt</c> on
/// the forge, for an agent that does not speak the protocol at all. A guide that drifts between those is
/// worse than no guide, because an agent cannot tell which copy is stale.
/// </para>
///
/// <para>
/// <see cref="Instructions"/> is deliberately short. It is sent at the start of every session whether it is
/// wanted or not, so it carries what changes an agent's behaviour and nothing else; the detail lives in a
/// topic the agent can ask for.
/// </para>
/// </summary>
public static class GraftGuide
{
    /// <summary>
    /// Sent during the protocol handshake. Describes the shape of the server and the handful of things that
    /// would otherwise be learned by failing.
    /// </summary>
    public const string Instructions = """
        Graft is a self-hosted Git forge with a GitLab-compatible REST API. These tools call that API with
        the caller's own personal access token, so everything here is bounded by what that token's owner can
        already do, and nothing is reachable that curl could not reach with the same credential.

        Start with graft_whoami. It reports which account you are acting as and whether writes are permitted,
        which saves discovering both by failing.

        Addressing a project. Three forms work everywhere a project is named: its path, like alice/widgets;
        its instance-wide slug, like widgets, which contains no account name and is the form to share; or its
        numeric id. Prefer the slug when passing a project address to a person.

        Permissions. Writes are refused entirely unless an administrator has turned them on for this server,
        and separately bounded by the token's scopes and the caller's role on each project. A refusal always
        names what would permit the action; read it rather than retrying another way.

        Reading a project. graft_list_projects shows what the token can see. graft_browse searches what the
        server publishes to anonymous readers, which is a different and usually smaller set. A 404 is
        deliberately ambiguous: a project you may not see and one that does not exist answer identically.

        Writing. graft_write_file replaces a whole file and commits it, so read the file first unless you
        intend to discard what is there. It creates the branch if it does not exist, which makes a branch of
        your own the safe default.

        Collaboration. A project has issues and merge requests, numbered from one within that project, and
        comments on both. Reading them needs whatever reading the repository needs, so a public project's
        issues are readable anonymously; writing on any of them needs membership, which reading a public
        project does not give you. graft_merge fast-forwards where it can and otherwise writes a merge
        commit, and refuses when both branches changed the same file rather than guessing.

        Planning. Labels are a project's own words and must exist before they are applied; graft_list_labels
        shows them and graft_triage applies them, with a milestone, assignees and a due date. Triaging needs
        the permission to push, or to be the author.

        Ask graft_help for detail on: addressing, permissions, visibility, pages, writing, collaboration,
        limits.
        """;

    /// <summary>The longer documents <c>graft_help</c> serves, keyed by topic.</summary>
    public static IReadOnlyDictionary<string, string> Topics { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["addressing"] = """
            # Addressing a project

            Any tool that takes a project accepts three forms:

            - A path, `alice/widgets`. The first segment is the owner's account name.
            - An instance-wide slug, `widgets`. Unique across the whole server and containing no account
              name, which is why it is the form to put in a link you give somebody.
            - A numeric id, `42`. Stable, but meaningless to a person.

            Every project has a slug, assigned when it was created from its own path. Where that name was
            already taken instance-wide, the project keeps its path and gains a short random suffix, so a
            second `widgets` becomes something like `widgets-3f9c`. Read it from `global_slug` on any project
            the tools return.

            The slug also addresses the project in a browser at `/projects/<slug>`, its published page at
            `/-/p/<slug>`, and a clone at `/projects/<slug>.git`. None of those names an account.
            """,

        ["permissions"] = """
            # What bounds an agent

            Three things, independently, and all of them must allow an action:

            1. **The server's agent switch.** An administrator decides whether agents may change anything at
               all. With it off, every mutating tool refuses and says so. Reading is unaffected.
            2. **The token's scopes.** `read_api` reads the API, `api` also writes, `read_repository` and
               `write_repository` cover repository content. A token missing a scope is refused with a
               message naming the scope.
            3. **The caller's role on the project.** Reporter reads, Developer pushes, Maintainer changes
               the project's settings. The project's owner and server administrators have all of them.

            Agents never administer accounts, never issue or revoke tokens, and never change the agent
            switches themselves. That is deliberate and no scope unlocks it: an agent that can mint a
            credential has no bounded blast radius, and one that can widen its own permissions makes the
            switch that bounds it decorative.

            A refusal always names what would change the answer. Read it instead of trying a different route
            to the same thing.
            """,

        ["visibility"] = """
            # Who can see a project

            A project is private, unlisted or public, and that setting is final: it is checked before
            anything else and takes precedence over a page's own settings.

            - **private**: only the owner, members and administrators.
            - **unlisted**: readable by anyone with the address, but absent from the public listing.
            - **public**: readable and listed.

            Separately, an operator decides whether this server publishes anything to anonymous readers at
            all. With that switch off a stored `public` is inert, so a project can read as public and still
            be invisible. That matters after a restore: a database from a server that published does not
            start publishing on one that does not.

            `graft_browse` searches the anonymous view, so it never reveals a private project even when the
            token could see it. `graft_list_projects` shows what the token can see. They answer different
            questions and the difference is the point.
            """,

        ["pages"] = """
            # Project pages

            A project page is a curated surface over a repository that can stay private: chosen files, a
            rendered readme, and optionally a static website.

            Two separate things have to be true. The owner publishes the page, which is a Maintainer act
            stored in the database. And the repository says what the page offers, in `.graft/project.json`
            on the default branch:

            ```json
            {
              "version": 1,
              "page": {
                "title": "Lanternry",
                "readme": "README.md",
                "files": ["README.md", "docs/**/*.md"],
                "site": { "root": "pages", "index": "index.html" }
              }
            }
            ```

            The config can only narrow, never publish, because authoring repository content needs Developer
            while publishing needs Maintainer. Everything is deny by default: no `files` means no files, and
            there is no "everything except" form. An invalid config unpublishes the page rather than falling
            back to defaults, so a config error takes the page down loudly.

            A page can never clone, serve history or show a diff, whatever the config says. Those walk the
            object graph, and no path allowlist can constrain a graph walk.

            A site is served only from a separate hostname an operator configures, because repository HTML
            cannot share an origin with the session cookie and the API. Within a site, a page can link back
            to the forge without naming a hostname by linking to `-/repo`.
            """,

        ["writing"] = """
            # Changing things

            Every mutating tool is refused unless an administrator has allowed writes on this server. Check
            with graft_whoami before planning work that depends on it.

            `graft_write_file` replaces a whole file and commits it. It does not patch, so read the file
            first with `graft_read_file` unless you mean to discard what is there. It creates the branch
            when it does not exist, which makes committing to a branch of your own the safe default rather
            than to the branch everybody else is using.

            `graft_update_project` changes a project's name, description and default branch. Only the fields
            you send are changed, so you need not resend the others and race whoever else is editing.

            `graft_set_visibility` and `graft_set_page_published` change who can see a project. Both widen
            access to strangers, so prefer to propose them rather than perform them unprompted.

            `graft_create_branch` points a new branch at a commit that already exists. It writes no content:
            use it to open a place to work, then `graft_write_file` to put something there. `graft_write_file`
            also creates a branch when the one it is given does not exist, so you need this only when you
            want to start from somewhere other than the default branch.

            There is no tool that deletes a project or a branch, and neither is an omission. The product can
            do both, from the browser or over the API with a person's own credential. An agent holding a
            token should not be one call away from ending a repository or removing the branch somebody is
            working on, so the capability stops at this boundary. Renaming a project does not exist at all.
            """,

        ["collaboration"] = """
            # Issues, merge requests and comments

            Both are numbered from one within their project, so `#1` means two different things in two
            projects and a number alone is never an address. The tools take the project and the number.

            Reading follows the repository: wherever the code can be read the conversation can be read,
            including anonymously on a public project. Writing does not follow it. Opening an issue,
            commenting, or proposing a merge all need membership of the project, so being able to read a
            public project is not being able to write on it. That is deliberate and it is why a read that
            succeeds can be followed by a write that is refused with a 403 rather than a 404.

            Editing somebody else's words needs Maintainer; closing and reopening needs the author or a
            Maintainer. A merged merge request is final and cannot be reopened, because reopening it would
            propose work the target branch already contains.

            ## Threads

            A comment can answer another: pass `reply_to` to `graft_comment` with the id the reading tools
            report. Threads are one level deep, so answering a reply joins the same thread. A thread can be
            marked resolved by whoever could push or whoever started it; there is no tool for that, because
            resolving is the reviewer's judgement that their point was met and the thing under review should
            not be the one to say so. Resolving hides nothing.

            ## Labels, milestones, assignees and due dates

            A project's labels are its own vocabulary, read with `graft_list_labels`; a label that does not
            exist is refused by name rather than created, so a typo is an error and not a new word. Milestones
            come from `graft_list_milestones`. `graft_triage` puts labels, a milestone, assignees and a due
            date on an issue or a merge request. The rule is that a Reporter files and a Developer organises:
            triaging needs the permission to push, or to be the author of the thing being triaged. Assignees
            must be members of the project.

            A confidential issue is readable by members and its author only, however public the project is,
            and is absent from a listing rather than marked. A merge request marked as a draft will not merge
            until its author, or a Maintainer, marks it ready; `graft_merge` reports that refusal in words.

            `graft_merge` is where Graft is narrower than other forges, and the narrowness is visible rather
            than hidden. It merges in two ways. If the target branch is an ancestor of the source it
            fast-forwards, moving the branch with no new commit. Otherwise it writes a merge commit with two
            parents, taking each side's changes, which it can do as long as no single file was changed on
            both sides. When one was, it refuses and names those files, because resolving that case means
            merging text inside a file and a forge that guessed at it would sometimes be confidently wrong.
            The answer then is to merge locally, where the conflict can be seen and resolved, and push.

            `graft_read_merge_request` reports `merge_method` before anything is merged, so you can tell
            which of those three a proposal is in without attempting it. A listing does not: deciding costs
            three tree walks per proposal, so a list reports `unchecked` and you read one to find out.

            ## Reviewing a line rather than a change

            `graft_comment` takes a file, a line and a side together, which puts the comment on that line of
            the proposal's diff. The paths and the line numbers come from `graft_read_merge_request`. Side
            is `old` for a line the change removed and `new` for one it added or left alone.

            A line comment records the commit the diff was computed from. When the branch is pushed to
            afterwards, the comment is reported as outdated rather than moved: the line it named may now be
            a different line, and quietly re-anchoring it would put your words against somebody else's code.
            Expect to re-read a proposal after a push rather than assuming your earlier comments still point
            where you left them.

            ## Approvals, and why you cannot give one

            A project can require a number of approvals before a merge request may be merged. An approval
            counts only while the commit it was given against is still the branch tip, so pushing to a
            proposal supersedes the approvals of what came before, and an author cannot approve their own.

            There is no tool to approve. That is deliberate: an approval exists to record that a person
            looked, and a gate an agent could satisfy would not be a gate. You can read how many are
            outstanding from `approvals_left`, and you can merge once people have approved, but the approval
            itself is theirs to give.
            """,

        ["limits"] = """
            # Limits worth knowing before you hit them

            - A 404 is deliberately ambiguous. A project you may not see and one that does not exist answer
              identically, so a 404 is never evidence that something is absent.
            - `graft_list_projects` returns what the token can see, not everything on the server.
            - A commit diff reports binary and oversized files rather than printing them, and says when it
              was truncated. A truncated diff that looked complete would be worse than one that admits it.
            - Repository files are read as text. A file that is not UTF-8 is reported as unreadable rather
              than returned as replacement characters.
            - This server may have been started without public browsing, in which case `graft_browse`
              answers 404 and that is configuration rather than failure.
            - Graft implements a bounded subset of the GitLab API. Routes it does not implement are refused
              by name rather than ignored, so an unexpected 400 usually means exactly what it says.
            """
    };

    /// <summary>The whole guide as one document, for <c>/llms.txt</c> and anything else that wants it in
    /// one piece rather than a topic at a time.</summary>
    public static string Full()
    {
        System.Text.StringBuilder text = new();
        // A reader here has no tools, so the framing the handshake can assume has to be said out loud:
        // the tool names below describe a surface they may not have, while everything else is the product.
        text.Append("""
            # Graft

            A self-hosted Git forge with a GitLab-compatible REST API, at /api/v4.

            This document is what Graft tells an AI agent about itself. An agent connected through the Model
            Context Protocol receives the overview below during its handshake and can ask for any topic with
            the graft_help tool; this page is the same material in one piece, for anything that reads a URL
            rather than speaking that protocol. Tool names appear throughout, and where you have no such
            tool the behaviour they describe is still the API's.


            """.ReplaceLineEndings("\n")).Append(Instructions).Append("\n\n");
        foreach (KeyValuePair<string, string> topic in Topics)
            text.Append(topic.Value).Append("\n\n");
        return text.ToString();
    }
}
