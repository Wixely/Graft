using Graft.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The Graft runner. Two commands:
//
//   graft-runner register --intonation <cuprinet://intone/...> --token <graft_rreg_...> --config <path> [--name <name>]
//   graft-runner run --config <path> [--once]
//
// `register` is what an install script calls: it checks the forge's signed Intonation, visits its Shrine, claims
// the one-time registration and writes the configuration the runner keeps using. `run` connects to the same
// Shrine, asks for work, runs it and reports back, until stopped. With `--once` it stops the first time the forge
// has nothing for it, which is how the acceptance suite drives it. Under the Windows service manager or systemd
// it runs as a service; anywhere else as an ordinary console process.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("graft-runner register --intonation <link> --token <token> --config <path> [--name <name>]");
    Console.WriteLine("graft-runner run --config <path> [--once]");
    Console.WriteLine("graft-runner version");
    return args.Length == 0 ? 2 : 0;
}

Dictionary<string, string> options = new(StringComparer.Ordinal);
HashSet<string> flags = new(StringComparer.Ordinal);
for (int index = 1; index < args.Length; index++)
{
    string name = args[index];
    if (!name.StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine("Unexpected argument: " + name); return 2; }
    if (name == "--once") { flags.Add(name); continue; }
    if (index + 1 >= args.Length) { Console.Error.WriteLine("Missing value for " + name); return 2; }
    options[name] = args[++index];
}

try
{
    switch (args[0])
    {
        case "version":
            Console.WriteLine("graft-runner " + RunnerAgent.Version + ", protocol " + Graft.RunnerProtocol.RunnerProtocol.Advertised);
            return 0;
        case "register":
            if (!options.TryGetValue("--intonation", out string? intonation) || !options.TryGetValue("--token", out string? token) ||
                !options.TryGetValue("--config", out string? registerConfig))
            {
                Console.Error.WriteLine("register needs --intonation, --token and --config.");
                return 2;
            }
            return await RunnerAgent.RegisterAsync(intonation, token, registerConfig,
                options.GetValueOrDefault("--name") ?? Environment.MachineName, CancellationToken.None);
        case "run":
            if (!options.TryGetValue("--config", out string? runConfig)) { Console.Error.WriteLine("run needs --config."); return 2; }
            RunnerConfig config = RunnerConfig.Load(runConfig);
            if (flags.Contains("--once"))
                return await new RunnerAgent(config, Path.GetDirectoryName(Path.GetFullPath(runConfig))!).RunAsync(once: true, CancellationToken.None);
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            builder.Services.AddWindowsService(service => service.ServiceName = "GraftRunner");
            builder.Services.AddSystemd();
            builder.Services.AddSingleton(new RunnerAgent(config, Path.GetDirectoryName(Path.GetFullPath(runConfig))!));
            builder.Services.AddHostedService<RunnerService>();
            await builder.Build().RunAsync();
            return 0;
        default:
            Console.Error.WriteLine("Unknown command: " + args[0]);
            return 2;
    }
}
catch (RunnerException error)
{
    Console.Error.WriteLine("graft-runner: " + error.Message);
    return 1;
}
