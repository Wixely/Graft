using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Graft.Runner;

/// <summary>
/// How a runner replaces itself with the build its forge serves when an administrator asks (ADR 0058).
///
/// <para>
/// The new executable is written beside the running one as <c>graft-runner.new</c>, run once with <c>version</c> to
/// see that it starts on this machine, and only then swapped in, the running file renamed to <c>graft-runner.old</c>
/// so it can be put back by hand. Both systems let a running executable be renamed, so the swap needs no helper and
/// never leaves the runner's own name empty for longer than one rename. The runner then ends, and comes back as the
/// new build the way it was started: systemd restarts it, the Windows service's recovery restarts it, and a runner
/// started by hand starts its replacement itself.
/// </para>
/// </summary>
internal static class SelfUpdate
{
    /// <summary>The exit code of a runner that has updated itself and is ending so the new build can start.</summary>
    public const int UpdatedExitCode = 3;

    /// <summary>
    /// The executable this runner was started from, when it is a runner's own; null when it runs under something else,
    /// such as <c>dotnet graft-runner.dll</c>, which it must not try to replace.
    /// </summary>
    public static string? Executable { get; } = FindExecutable();

    /// <summary>The SHA-256 of <see cref="Executable"/> as it was when the runner started, which the forge compares with what it serves.</summary>
    public static string? ExecutableSha256 { get; } = Executable is null ? null : Hash(Executable);

    private static string? FindExecutable()
    {
        string? path = Environment.ProcessPath;
        return path is not null && Path.GetFileNameWithoutExtension(path).StartsWith("graft-runner", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string? Hash(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Puts <paramref name="downloaded"/>, already checked against the SHA-256 the forge declared, in place of the
    /// running executable, and returns what the new build says it is. Throws <see cref="RunnerException"/> saying why
    /// not, leaving the running executable where it was.
    /// </summary>
    public static async Task<string> InstallAsync(string downloaded, CancellationToken cancellationToken)
    {
        string executable = Executable ?? throw new RunnerException("this runner is not running from its own executable, so it cannot replace it");
        string directory = Path.GetDirectoryName(executable)!;
        string stem = Path.GetFileNameWithoutExtension(executable);
        string extension = Path.GetExtension(executable);
        string staged = Path.Combine(directory, stem + ".new" + extension);
        string old = Path.Combine(directory, stem + ".old" + extension);
        try
        {
            File.Move(downloaded, staged, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RunnerException("could not write the new build beside " + executable + ": " + exception.Message);
        }

        string version;
        try { version = await ProbeAsync(staged, cancellationToken); }
        catch
        {
            TryDelete(staged);
            throw;
        }

        try
        {
            File.Move(executable, old, overwrite: true);
            try { File.Move(staged, executable); }
            catch
            {
                File.Move(old, executable);
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(staged);
            throw new RunnerException("could not replace " + executable + ": " + exception.Message);
        }
        return version;
    }

    /// <summary>Runs the new build with <c>version</c>; it must answer as a Graft runner within half a minute.</summary>
    private static async Task<string> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(path)
        {
            ArgumentList = { "version" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        Process process;
        try { process = Process.Start(start) ?? throw new RunnerException("the new build did not start"); }
        catch (System.ComponentModel.Win32Exception exception) { throw new RunnerException("the new build does not start on this machine: " + exception.Message); }
        using (process)
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(limit.Token);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new RunnerException("the new build did not answer within 30 seconds");
            }
            string said = (await output).Trim();
            if (process.ExitCode != 0 || !said.StartsWith("graft-runner ", StringComparison.Ordinal))
                throw new RunnerException("the new build does not start on this machine (exit " + process.ExitCode + "): " + (said + " " + (await errors).Trim()).Trim());
            return said;
        }
    }

    /// <summary>
    /// After the swap, before this process ends: sees that the new build will start in its place. Under systemd the
    /// unit's <c>Restart=always</c> does it. Under the Windows service manager the service's recovery should, and a
    /// small PowerShell command started here starts the service if it is still stopped ten seconds on, for a service
    /// installed without recovery. Started by hand, the runner starts its replacement with the same arguments.
    /// </summary>
    public static void Restart(string[] args)
    {
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        {
            ProcessStartInfo restarter = new("powershell.exe")
            {
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "Start-Sleep -Seconds 10; if ((Get-Service -Name 'GraftRunner').Status -eq 'Stopped') { Start-Service -Name 'GraftRunner' }"
                },
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            try { Process.Start(restarter)?.Dispose(); }
            catch (System.ComponentModel.Win32Exception exception) { Console.Error.WriteLine("Could not start the service restarter: " + exception.Message); }
            return;
        }
        if (SystemdHelpers.IsSystemdService()) return;
        ProcessStartInfo replacement = new(Executable!) { UseShellExecute = false };
        foreach (string arg in args) replacement.ArgumentList.Add(arg);
        using Process? started = Process.Start(replacement);
        Console.WriteLine("Started the new build as process " + (started?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?") + ".");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
