// git-remote-graft: Git's remote helper and Git LFS transfer agent for a Graft forge behind a proxy that limits how
// large one request may be (ADR 0043). Run by Git, not by hand, except for setting it up:
//
//   git-remote-graft setup https://forge.example [--push-only]   route this forge's Git and LFS traffic through it
//   git-remote-graft remove https://forge.example                undo that
//   git remote-graft <remote> <address>                          what Git runs for graft::<address>
//   git-remote-graft lfs-agent                                   what git-lfs runs for the graft transfer

using Graft.Remote;

using CancellationTokenSource stopping = new();
Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; stopping.Cancel(); };

try
{
    if (args is ["--version"] or ["version"])
    {
        Console.WriteLine("git-remote-graft " + HelperVersion.Text);
        return 0;
    }
    if (args is ["lfs-agent"]) return await LfsAgent.RunAsync(stopping.Token);
    if (args is ["setup", string forge, .. string[] options] && options.All(option => option == "--push-only"))
        return Setup.Install(forge, pushOnly: options.Length > 0);
    if (args is ["remove", string removed]) return Setup.Remove(removed);
    if (args is [string remote, string address] || args is [string only] && (address = remote = only) is not null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? target) || target.Scheme is not ("https" or "http"))
            throw new HelperException("'" + address + "' is not an https address; write the remote as graft::https://...");
        GitBridge bridge = new(remote, target, Console.OpenStandardInput(), Console.OpenStandardOutput());
        return await bridge.RunAsync(stopping.Token);
    }
    Console.Error.WriteLine("usage: git-remote-graft setup <https origin> [--push-only] | remove <https origin> | --version");
    Console.Error.WriteLine("Git runs it for a remote written graft::https://..., and git-lfs as the graft transfer agent.");
    return 2;
}
catch (HelperException error)
{
    Console.Error.WriteLine("fatal: " + error.Message);
    return 128;
}
catch (HttpRequestException error)
{
    Console.Error.WriteLine("fatal: could not reach the forge: " + error.Message);
    return 128;
}
catch (OperationCanceledException)
{
    return 130;
}
