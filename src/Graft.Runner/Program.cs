using Graft.Runner;
using Microsoft.Extensions.Configuration;
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
            // The content root is the runner's own directory, not the working directory. systemd starts a unit in /,
            // and a host rooted there watches the whole filesystem for configuration changes and never finishes
            // starting, so the runner neither asked for work nor told systemd it was ready. Nothing is watched at all:
            // the runner reads no configuration files of the host's, and its directory holds the jobs' work.
            ConfigurationManager hostConfiguration = new();
            hostConfiguration["hostBuilder:reloadConfigOnChange"] = "false";
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = Path.GetDirectoryName(Path.GetFullPath(runConfig))!,
                Configuration = hostConfiguration
            });
            builder.Services.AddWindowsService(service => service.ServiceName = "GraftRunner");
            builder.Services.AddSystemd();
            builder.Services.AddSingleton(new RunnerAgent(config, Path.GetDirectoryName(Path.GetFullPath(runConfig))!));
            builder.Services.AddHostedService<RunnerService>();
            IHost host = builder.Build();
            // The unit the install script writes is Type=notify. The hosting library's own notification did not
            // reach systemd, under the runtime or the static build, so systemd killed the runner at its start
            // timeout; the forge met the same thing and says it is ready itself, and so does the runner.
            IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStarted.Register(() => SystemdNotify.Send("READY=1\nSTATUS=Graft runner is asking for work"));
            lifetime.ApplicationStopping.Register(() => SystemdNotify.Send("STOPPING=1\nSTATUS=Graft runner is stopping"));
            await host.RunAsync();
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
