using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CupriNet.Abstractions;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Graft.Contracts;
using Microsoft.Extensions.Hosting;
using Protocol = Graft.RunnerProtocol.RunnerProtocol;

namespace Graft.Runner;

/// <summary>A failure the runner reports in a sentence and exits on.</summary>
public sealed class RunnerException(string message) : Exception(message);

/// <summary>Where the forge is, how to prove the runner is the one it registered, and nothing more.</summary>
public sealed record RunnerConfig(
    [property: JsonPropertyName("network")] string Network,
    [property: JsonPropertyName("shrine_address")] string ShrineAddress,
    [property: JsonPropertyName("shrine_sigil")] string ShrineSigil,
    [property: JsonPropertyName("endpoints")] RunnerEndpoint[] Endpoints,
    [property: JsonPropertyName("runner_id")] long RunnerId,
    [property: JsonPropertyName("credential")] string Credential,
    [property: JsonPropertyName("name")] string Name)
{
    public static RunnerConfig Load(string path)
    {
        if (!File.Exists(path)) throw new RunnerException("There is no runner configuration at " + path + "; register first.");
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), RunnerJson.Default.RunnerConfig)
                ?? throw new RunnerException("The runner configuration at " + path + " is empty.");
        }
        catch (JsonException) { throw new RunnerException("The runner configuration at " + path + " is not valid JSON."); }
    }

    /// <summary>Written so only the account that registered can read it: it holds the runner's credential.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        FileStreamOptions options = new() { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (FileStream stream = new(temporary, options))
            JsonSerializer.Serialize(stream, this, RunnerJson.Default.RunnerConfig);
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed record RunnerEndpoint([property: JsonPropertyName("host")] string Host, [property: JsonPropertyName("port")] int Port);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RunnerConfig))]
internal sealed partial class RunnerJson : JsonSerializerContext;

/// <summary>
/// The runner: visits the forge's Shrine, asks for a job, runs it in a fresh directory, streams its output back,
/// reports the result, and asks again.
///
/// <para>
/// The forge's identity is the Shrine key pinned at registration, so a man in the middle cannot answer as the forge
/// and no certificate authority or DNS is involved. The runner's identity is the credential the forge issued, sent
/// with every request; the CupriNet visit itself is anonymous, by design, and grants nothing on its own.
/// </para>
/// </summary>
public sealed class RunnerAgent(RunnerConfig config, string home)
{
    public const string Version = "0.1.0";

    /// <summary>A job runs at most this long before it is stopped and failed.</summary>
    private static readonly TimeSpan JobTimeout = TimeSpan.FromHours(1);

    private static readonly BouncyCastleSuite Suite = new();
    private readonly ForgeLink link = new(config);

    // ------------------------------------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------------------------------------

    public static async Task<int> RegisterAsync(string intonation, string token, string configPath, string name, CancellationToken cancellationToken)
    {
        IntonationValidation validation = IntonationValidator.ValidateUri(intonation.Trim(), new Concordium(Protocol.Network), Suite, DateTimeOffset.UtcNow);
        if (!validation.IsValid || validation.Intonation is null)
            throw new RunnerException("The Intonation in this install script is not usable (" + validation.Status + "). " +
                "If it has expired, create a new runner in Graft and use its new script.");
        Intonation link = validation.Intonation;
        if (link.Shrine is not Sigil shrine) throw new RunnerException("The Intonation names no Graft Shrine; it is not from a Graft forge.");
        RunnerEndpoint[] endpoints = [.. link.Beacons.Where(beacon => beacon.Kind != EndpointKind.Onion).Select(beacon => new RunnerEndpoint(beacon.Host, beacon.Port))];
        if (endpoints.Length == 0) throw new RunnerException("The Intonation carries no address this runner can reach.");

        RunnerConfig pending = new(Protocol.Network, link.ShrineAddress!, Convert.ToBase64String(shrine.Span), endpoints, 0, "", name);
        await using ShrineSession session = await ForgeLink.ConnectAsync(pending, cancellationToken);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new RunnerRegisterRequest(token.Trim(), name, Platform(), Version), ApiJson.Default.RunnerRegisterRequest);
        OracleResponse response = await session.ConsultAsync(Request(Protocol.Routes.Register, body, null), cancellationToken);
        if (response.Status != 201) throw new RunnerException("The forge refused the registration: " + ErrorText(response));
        RunnerRegisterResponse registered = JsonSerializer.Deserialize(response.Body, ApiJson.Default.RunnerRegisterResponse)
            ?? throw new RunnerException("The forge's answer to the registration was empty.");
        (pending with { RunnerId = registered.RunnerId, Credential = registered.Credential }).Save(configPath);
        Console.WriteLine("Registered as runner #" + registered.RunnerId + " with " + link.ShrineAddress + ", protocol " + registered.Protocol + ".");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------
    // Running
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Asks for work until cancelled, reconnecting after a dropped session. With <paramref name="once"/>, returns the
    /// first time the forge has nothing to hand out: 0 if every job it ran succeeded, 1 otherwise.
    /// </summary>
    public async Task<int> RunAsync(bool once, CancellationToken cancellationToken)
    {
        bool allSucceeded = true;
        TimeSpan backoff = TimeSpan.FromSeconds(2);
        await using ForgeLink owned = link;
        while (!cancellationToken.IsCancellationRequested)
        {
            OracleResponse response;
            try { response = await link.ConsultAsync(Request(Protocol.Routes.RequestJob, [], config.Credential), cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException and not RunnerException)
            {
                if (once) throw new RunnerException("Could not reach the forge: " + exception.Message);
                Console.Error.WriteLine("Could not reach the forge (" + exception.Message + "); trying again in " + backoff.TotalSeconds + " s.");
                await Task.Delay(backoff, cancellationToken);
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                continue;
            }
            backoff = TimeSpan.FromSeconds(2);
            if (response.Status == 204)
            {
                if (once) return allSucceeded ? 0 : 1;
                continue;
            }
            if (response.Status == 401) throw new RunnerException("The forge no longer accepts this runner's credential: " + ErrorText(response));
            if (response.Status == 426) throw new RunnerException("This runner and the forge share no protocol version: " + ErrorText(response));
            if (response.Status != 200)
            {
                if (once) throw new RunnerException("The forge answered " + response.Status + ": " + ErrorText(response));
                Console.Error.WriteLine("The forge answered " + response.Status + " (" + ErrorText(response) + "); asking again.");
                await Task.Delay(backoff, cancellationToken);
                continue;
            }
            RunnerJobResponse job = JsonSerializer.Deserialize(response.Body, ApiJson.Default.RunnerJobResponse)
                ?? throw new RunnerException("The forge sent an empty job.");
            allSucceeded &= await RunJobAsync(job, cancellationToken);
        }
        return allSucceeded ? 0 : 1;
    }

    /// <summary>Runs one job to its end and reports it. True when it succeeded.</summary>
    private async Task<bool> RunJobAsync(RunnerJobResponse job, CancellationToken cancellationToken)
    {
        Console.WriteLine("Job " + job.JobId + " (" + job.Name + ") for " + job.Project + " at " + job.Sha[..8]);
        string workspace = Path.Combine(home, "builds", job.JobId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        TraceWriter trace = new(link, job);
        RunnerTransfers transfers = new(link, home, trace.Line);
        int? exitCode = null;
        string? failure = null;
        string? source = null;
        string? artifacts = null;
        try
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            Directory.CreateDirectory(workspace);
            trace.Line("Graft runner " + Version + " on " + config.Name + " (" + Platform() + ")");
            trace.Line("Fetching " + job.Project + " at " + job.Sha + ", " + job.Source.Size + " bytes" + (job.Source.Chunked ? ", chunked" : ""));
            source = await transfers.DownloadAsync(job.Source, cancellationToken);
            await using (FileStream compressed = File.OpenRead(source))
            await using (GZipStream tar = new(compressed, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(tar, workspace, overwriteFiles: true, cancellationToken);
            File.Delete(source);
            exitCode = await ExecuteAsync(job, workspace, trace, cancellationToken);
            if (exitCode != 0) failure = "The script exited with " + exitCode + ".";
            else if (job.Artifacts.Length > 0)
            {
                // As GitLab's default: artifacts are kept from a job that succeeded.
                trace.Line("Uploading artifacts");
                artifacts = transfers.CollectArtifacts(job.JobId, workspace, job.Artifacts);
                if (artifacts is not null)
                {
                    await transfers.UploadAsync(job.JobId, job.JobToken, artifacts, cancellationToken);
                    trace.Line("Artifacts uploaded");
                }
            }
        }
        catch (RunnerException exception)
        {
            failure = exception.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failure = "The job ran past its time limit of " + JobTimeout.TotalMinutes + " minutes and was stopped.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = "The runner could not run the job: " + exception.Message;
        }
        if (failure is not null) trace.Line("ERROR: " + failure);
        else trace.Line("Job succeeded");
        await trace.FlushAsync(cancellationToken);
        bool succeeded = failure is null;
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new RunnerFinishRequest(succeeded ? "success" : "failed", exitCode, failure), ApiJson.Default.RunnerFinishRequest);
        // The result matters more than anything else the runner sends, so it is retried over new connections.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                OracleResponse finished = await link.ConsultAsync(Request(Protocol.Routes.JobPrefix + job.JobId + "/finish", body, job.JobToken), cancellationToken);
                if (finished.Status != 200) Console.Error.WriteLine("The forge did not accept the result of job " + job.JobId + ": " + ErrorText(finished));
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt < 8)
            {
                Console.Error.WriteLine("Could not report job " + job.JobId + " (" + exception.Message + "); trying again.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 2)), cancellationToken);
            }
        }
        foreach (string? leftover in new[] { source, artifacts })
            if (leftover is not null) try { File.Delete(leftover); } catch (IOException) { }
        try { Directory.Delete(workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Console.WriteLine("Job " + job.JobId + (succeeded ? " succeeded" : " failed"));
        return succeeded;
    }

    /// <summary>
    /// Runs the script as one shell session, as GitLab does: PowerShell on Windows, <c>sh</c> elsewhere. Each line
    /// is echoed before it runs and the session stops at the first line that fails.
    /// </summary>
    private static async Task<int> ExecuteAsync(RunnerJobResponse job, string workspace, TraceWriter trace, CancellationToken cancellationToken)
    {
        bool windows = OperatingSystem.IsWindows();
        string scriptPath = Path.Combine(workspace, windows ? ".graft-job.ps1" : ".graft-job.sh");
        StringBuilder script = new();
        if (windows)
        {
            script.Append("$ErrorActionPreference = 'Stop'\n");
            foreach (string line in job.Script)
            {
                script.Append("Write-Host ('$ ' + ").Append(PowerShellQuote(line)).Append(")\n");
                script.Append(line).Append('\n');
                script.Append("if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\n");
            }
            script.Append("exit 0\n");
        }
        else
        {
            script.Append("set -e\n");
            foreach (string line in job.Script)
            {
                script.Append("printf '%s\\n' ").Append(ShellQuote("$ " + line)).Append('\n');
                script.Append(line).Append('\n');
            }
        }
        await File.WriteAllTextAsync(scriptPath, script.ToString(), new UTF8Encoding(false), cancellationToken);

        ProcessStartInfo start = windows
            ? new("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath } }
            : new("/bin/sh") { ArgumentList = { scriptPath } };
        start.WorkingDirectory = workspace;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        foreach ((string key, string value) in job.Variables) start.Environment[key] = value;
        start.Environment["CI"] = "true";
        start.Environment["GRAFT_CI"] = "true";
        start.Environment["CI_JOB_ID"] = job.JobId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["CI_JOB_NAME"] = job.Name;
        start.Environment["CI_JOB_STAGE"] = job.Stage;
        start.Environment["CI_COMMIT_SHA"] = job.Sha;
        start.Environment["CI_COMMIT_SHORT_SHA"] = job.Sha[..8];
        start.Environment["CI_COMMIT_REF_NAME"] = job.Ref;
        start.Environment["CI_PROJECT_PATH"] = job.Project;
        start.Environment["CI_PROJECT_DIR"] = workspace;

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("the shell did not start");
        process.StandardInput.Close();
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(JobTimeout);
        Task output = Pump(process.StandardOutput, trace);
        Task errors = Pump(process.StandardError, trace);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await Task.WhenAll(output, errors);
        return process.ExitCode;
    }

    private static async Task Pump(StreamReader reader, TraceWriter trace)
    {
        while (await reader.ReadLineAsync() is string line) trace.Line(line);
    }

    // ------------------------------------------------------------------------------------------------
    // The connection
    // ------------------------------------------------------------------------------------------------

    private static OracleRequest Request(string path, byte[] body, string? authorization) => RunnerTransfers.Request(path, body, authorization);

    private static string ErrorText(OracleResponse response)
    {
        try
        {
            ErrorResponse? error = JsonSerializer.Deserialize(response.Body, ApiJson.Default.ErrorResponse);
            if (error?.Message is { Length: > 0 } message) return message;
        }
        catch (JsonException) { }
        return "status " + response.Status;
    }

    private static string Platform() =>
        (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other") +
        "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Collects a job's output and sends it to the forge in order, a chunk at a time, at the offset the forge
    /// expects, so a retried chunk is recognised rather than duplicated.
    /// </summary>
    private sealed class TraceWriter
    {
        private readonly ForgeLink link;
        private readonly RunnerJobResponse job;
        private readonly StringBuilder pending = new();
        private readonly object gate = new();
        private readonly Timer timer;
        private long offset;
        private Task sending = Task.CompletedTask;

        public TraceWriter(ForgeLink link, RunnerJobResponse job)
        {
            this.link = link;
            this.job = job;
            timer = new Timer(_ => Kick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public void Line(string text)
        {
            Console.WriteLine("  " + text);
            lock (gate) pending.Append(text).Append('\n');
        }

        private void Kick()
        {
            lock (gate)
            {
                if (pending.Length == 0 || !sending.IsCompleted) return;
                sending = SendAsync(CancellationToken.None);
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            await timer.DisposeAsync();
            Task inFlight;
            lock (gate) inFlight = sending;
            await inFlight;
            // Everything left, a chunk at a time; a chunk that fails to send is put back, so stop after a few tries.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                bool empty;
                lock (gate) empty = pending.Length == 0;
                if (empty) return;
                await SendAsync(cancellationToken);
            }
        }

        private async Task SendAsync(CancellationToken cancellationToken)
        {
            string chunk;
            lock (gate)
            {
                if (pending.Length == 0) return;
                chunk = pending.Length > 64 * 1024 ? pending.ToString(0, 64 * 1024) : pending.ToString();
                pending.Remove(0, chunk.Length);
            }
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new RunnerTraceRequest(offset, chunk), ApiJson.Default.RunnerTraceRequest);
            try
            {
                OracleResponse response = await link.ConsultAsync(Request(Protocol.Routes.JobPrefix + job.JobId + "/trace", body, job.JobToken), cancellationToken);
                if (response.Status == 202) offset += chunk.Length;
                else Console.Error.WriteLine("The forge did not take a piece of the log: " + ErrorText(response));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Put it back so the next send carries it; the offset has not moved.
                lock (gate) pending.Insert(0, chunk);
                Console.Error.WriteLine("Could not send the log: " + exception.Message);
            }
        }
    }
}

/// <summary>The runner under a service manager: runs until the service is stopped.</summary>
internal sealed class RunnerService(RunnerAgent agent) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await agent.RunAsync(once: false, stoppingToken); }
        catch (OperationCanceledException) { }
        catch (RunnerException error) { Console.Error.WriteLine("graft-runner: " + error.Message); throw; }
    }
}
