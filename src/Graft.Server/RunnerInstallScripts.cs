using System.Globalization;
using System.Text;

namespace Graft.Server;

/// <summary>
/// The install scripts a runner is set up with: one for Windows PowerShell, one for a POSIX shell. Each carries
/// the Intonation the runner connects with and the one-time token it registers with, so installing a runner is
/// running one file on the machine that will run the jobs.
///
/// <para>
/// A script holds a secret until it is used: the token claims the registration once and is then spent, and it
/// expires a day after it was made either way. The Intonation is not a secret; it is the forge's signed address
/// and expires in step with the token. The scripts never put either on a command line another user could read
/// longer than the one registration call that needs them.
/// </para>
/// </summary>
internal static class RunnerInstallScripts
{
    /// <summary>The name a runner binary is served under for a platform, as the scripts ask for it.</summary>
    public static string BinaryName(string platform) => "graft-runner-" + platform + (platform.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "");

    /// <summary>The platforms the download route answers for; anything else is not found.</summary>
    public static readonly string[] Platforms = ["win-x64", "linux-x64"];

    public static string PowerShell(string origin, long runnerId, string description, string intonation, string token, DateTimeOffset expires, bool downloads)
    {
        StringBuilder script = new();
        script.Append("# Graft runner installer for ").Append(origin).Append('\n');
        script.Append("# Runner #").Append(runnerId.ToString(CultureInfo.InvariantCulture)).Append(description.Length == 0 ? "" : " (" + Comment(description) + ")")
            .Append(". Generated ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).Append('.').Append('\n');
        script.Append("# It carries a one-time registration token, valid until ").Append(expires.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .Append(". Keep it private until it has run.\n");
        script.Append("# Run as an administrator for a service; -NoService only registers. -Binary uses a runner you already have.\n");
        script.Append("param(\n");
        script.Append("    [string]$InstallDir = (Join-Path $env:ProgramData 'GraftRunner'),\n");
        script.Append("    [string]$Binary = '',\n");
        script.Append("    [switch]$NoService\n");
        script.Append(")\n");
        script.Append("$ErrorActionPreference = 'Stop'\n");
        script.Append("$Intonation = ").Append(PowerShellQuote(intonation)).Append('\n');
        script.Append("$Token = ").Append(PowerShellQuote(token)).Append('\n');
        script.Append("$Origin = ").Append(PowerShellQuote(origin)).Append('\n');
        script.Append("New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null\n");
        script.Append("if ($Binary -eq '') {\n");
        if (downloads)
        {
            script.Append("    if (-not [Environment]::Is64BitOperatingSystem) { throw 'The Graft runner needs 64-bit Windows.' }\n");
            script.Append("    $Binary = Join-Path $InstallDir 'graft-runner.exe'\n");
            script.Append("    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12\n");
            script.Append("    Invoke-WebRequest -UseBasicParsing -Uri \"$Origin/-/runner/download/").Append(BinaryName("win-x64")).Append("\" -OutFile $Binary\n");
        }
        else
        {
            script.Append("    throw 'This forge does not serve runner binaries. Build or copy graft-runner.exe and pass it with -Binary.'\n");
        }
        script.Append("}\n");
        script.Append("$Config = Join-Path $InstallDir 'runner.json'\n");
        script.Append("& $Binary register --intonation $Intonation --token $Token --name $env:COMPUTERNAME --config $Config\n");
        script.Append("if ($LASTEXITCODE -ne 0) { throw \"Registration failed (exit $LASTEXITCODE).\" }\n");
        script.Append("if ($NoService) {\n");
        script.Append("    Write-Host \"Registered. Start the runner with: & '$Binary' run --config '$Config'\"\n");
        script.Append("    exit 0\n");
        script.Append("}\n");
        script.Append("$existing = Get-Service -Name 'GraftRunner' -ErrorAction SilentlyContinue\n");
        script.Append("if ($existing) { Stop-Service -Name 'GraftRunner' -ErrorAction SilentlyContinue; sc.exe delete GraftRunner | Out-Null; Start-Sleep -Seconds 2 }\n");
        script.Append("New-Service -Name 'GraftRunner' -DisplayName 'Graft runner' -StartupType Automatic -BinaryPathName ('\"' + $Binary + '\" run --config \"' + $Config + '\"') | Out-Null\n");
        AppendRecovery(script);
        script.Append("Start-Service -Name 'GraftRunner'\n");
        script.Append("Write-Host 'The Graft runner is installed and running as the GraftRunner service.'\n");
        return script.ToString();
    }

    public static string Shell(string origin, long runnerId, string description, string intonation, string token, DateTimeOffset expires, bool downloads)
    {
        StringBuilder script = new();
        script.Append("#!/bin/sh\n");
        script.Append("# Graft runner installer for ").Append(origin).Append('\n');
        script.Append("# Runner #").Append(runnerId.ToString(CultureInfo.InvariantCulture)).Append(description.Length == 0 ? "" : " (" + Comment(description) + ")")
            .Append(". Generated ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).Append(".\n");
        script.Append("# It carries a one-time registration token, valid until ").Append(expires.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .Append(". Keep it private until it has run.\n");
        script.Append("# Run as root for a systemd service; --no-service only registers. --binary uses a runner you already have.\n");
        script.Append("set -eu\n");
        script.Append("INTONATION=").Append(ShellQuote(intonation)).Append('\n');
        script.Append("TOKEN=").Append(ShellQuote(token)).Append('\n');
        script.Append("ORIGIN=").Append(ShellQuote(origin)).Append('\n');
        script.Append("DIR=/opt/graft-runner\n");
        script.Append("BINARY=''\n");
        script.Append("SERVICE=1\n");
        script.Append("while [ $# -gt 0 ]; do\n");
        script.Append("    case \"$1\" in\n");
        script.Append("        --dir) DIR=$2; shift 2 ;;\n");
        script.Append("        --binary) BINARY=$2; shift 2 ;;\n");
        script.Append("        --no-service) SERVICE=0; shift ;;\n");
        script.Append("        *) echo \"Unknown option: $1\" >&2; exit 2 ;;\n");
        script.Append("    esac\n");
        script.Append("done\n");
        // Checked before registering, because registering spends the token: a script that registered and then found
        // no systemd could not be run again. WSL is the usual case of a Linux without systemd running.
        script.Append("WSL=0\n");
        script.Append("if grep -qi microsoft /proc/version 2>/dev/null; then WSL=1; fi\n");
        script.Append("if [ \"$SERVICE\" = 1 ] && [ ! -d /run/systemd/system ]; then\n");
        script.Append("    echo 'systemd is not running here, so the runner cannot be installed as a service. Nothing was registered.' >&2\n");
        script.Append("    if [ \"$WSL\" = 1 ]; then\n");
        script.Append("        echo 'Under WSL, add these lines to /etc/wsl.conf, run \"wsl --shutdown\" from Windows, and run this script again:' >&2\n");
        script.Append("        echo '    [boot]' >&2\n");
        script.Append("        echo '    systemd=true' >&2\n");
        script.Append("    fi\n");
        script.Append("    echo 'Or run it with --no-service and start the runner yourself.' >&2\n");
        script.Append("    exit 1\n");
        script.Append("fi\n");
        script.Append("mkdir -p \"$DIR\"\n");
        script.Append("if [ -z \"$BINARY\" ]; then\n");
        if (downloads)
        {
            script.Append("    case \"$(uname -m)\" in\n");
            script.Append("        x86_64|amd64) PLATFORM=linux-x64 ;;\n");
            script.Append("        *) echo \"The Graft runner is built for x86_64 Linux; this machine is $(uname -m).\" >&2; exit 1 ;;\n");
            script.Append("    esac\n");
            script.Append("    BINARY=\"$DIR/graft-runner\"\n");
            script.Append("    URL=\"$ORIGIN/-/runner/download/graft-runner-$PLATFORM\"\n");
            script.Append("    if command -v curl >/dev/null 2>&1; then curl -fsSL -o \"$BINARY\" \"$URL\"\n");
            script.Append("    elif command -v wget >/dev/null 2>&1; then wget -q -O \"$BINARY\" \"$URL\"\n");
            script.Append("    else echo 'Neither curl nor wget is installed; download the runner yourself and pass it with --binary.' >&2; exit 1\n");
            script.Append("    fi\n");
            script.Append("    chmod 755 \"$BINARY\"\n");
        }
        else
        {
            script.Append("    echo 'This forge does not serve runner binaries. Build or copy graft-runner and pass it with --binary.' >&2\n");
            script.Append("    exit 1\n");
        }
        script.Append("fi\n");
        script.Append("CONFIG=\"$DIR/runner.json\"\n");
        script.Append("\"$BINARY\" register --intonation \"$INTONATION\" --token \"$TOKEN\" --name \"$(hostname)\" --config \"$CONFIG\"\n");
        script.Append("if [ \"$SERVICE\" = 0 ]; then\n");
        script.Append("    echo \"Registered. Start the runner with: $BINARY run --config $CONFIG\"\n");
        script.Append("    exit 0\n");
        script.Append("fi\n");
        script.Append("cat > /etc/systemd/system/graft-runner.service <<UNIT\n");
        script.Append("[Unit]\n");
        script.Append("Description=Graft runner\n");
        script.Append("After=network-online.target\n");
        script.Append("Wants=network-online.target\n");
        script.Append("\n");
        script.Append("[Service]\n");
        script.Append("Type=notify\n");
        script.Append("WorkingDirectory=$DIR\n");
        script.Append("ExecStart=$BINARY run --config $CONFIG\n");
        script.Append("Restart=always\n");
        script.Append("RestartSec=5\n");
        script.Append("\n");
        script.Append("[Install]\n");
        script.Append("WantedBy=multi-user.target\n");
        script.Append("UNIT\n");
        script.Append("systemctl daemon-reload\n");
        script.Append("systemctl enable --now graft-runner\n");
        script.Append("echo 'The Graft runner is installed and running as the graft-runner service.'\n");
        // WSL stops a distribution once no Windows program is using it, and a systemd service does not count, so
        // the runner would stop with it. Starting a process that never exits from Windows at sign-in keeps it up.
        script.Append("if [ \"$WSL\" = 1 ]; then\n");
        script.Append("    echo 'Under WSL this distribution stops when nothing on Windows is using it, and the runner stops with it.' >&2\n");
        script.Append("    echo \"To keep it running, have Windows run this at startup, for example from Task Scheduler:\" >&2\n");
        script.Append("    echo \"    wsl.exe -d ${WSL_DISTRO_NAME:-<distribution>} --exec sleep infinity\" >&2\n");
        script.Append("fi\n");
        return script.ToString();
    }

    /// <summary>
    /// The service restarts whenever it ends other than by being stopped, as systemd's <c>Restart=always</c> does on
    /// Linux. A runner that has updated itself ends with a code that says so, and this is what brings it back.
    /// </summary>
    private static void AppendRecovery(StringBuilder script)
    {
        script.Append("sc.exe failure GraftRunner reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null\n");
        script.Append("sc.exe failureflag GraftRunner 1 | Out-Null\n");
    }

    /// <summary>
    /// The Windows update script (ADR 0058): replaces the installed runner's executable with the one this forge serves
    /// and restarts the service, keeping the runner's registration. It carries no secret. The new executable is run
    /// once before anything is replaced, and the old one is kept beside it as <c>graft-runner.old.exe</c>.
    /// </summary>
    public static string UpdatePowerShell(string origin, bool downloads)
    {
        StringBuilder script = new();
        script.Append("# Graft runner update for ").Append(origin).Append('\n');
        script.Append("# Replaces the runner's executable with the one this forge serves and restarts its service, keeping its\n");
        script.Append("# registration. Run as an administrator. -InstallDir names a runner installed elsewhere; -Binary uses a\n");
        script.Append("# runner you already have. A job the runner is running when the service stops is stopped with it.\n");
        script.Append("param(\n");
        script.Append("    [string]$InstallDir = (Join-Path $env:ProgramData 'GraftRunner'),\n");
        script.Append("    [string]$Binary = ''\n");
        script.Append(")\n");
        script.Append("$ErrorActionPreference = 'Stop'\n");
        script.Append("$Origin = ").Append(PowerShellQuote(origin)).Append('\n');
        // The service is only stopped and started when it runs the very executable being replaced.
        script.Append("$ServicePath = $null\n");
        script.Append("$Service = Get-CimInstance -ClassName Win32_Service -Filter \"Name='GraftRunner'\"\n");
        script.Append("if ($Service -and $Service.PathName -match '^\\s*\"([^\"]+)\"') { $ServicePath = $Matches[1] }\n");
        script.Append("$Target = Join-Path $InstallDir 'graft-runner.exe'\n");
        script.Append("if ($ServicePath -and -not $PSBoundParameters.ContainsKey('InstallDir')) { $Target = $ServicePath }\n");
        script.Append("if (-not (Test-Path -LiteralPath $Target)) { throw \"There is no Graft runner at $Target. Pass -InstallDir, or install one with a script from the forge's Runners page.\" }\n");
        script.Append("$Managed = [bool]$ServicePath -and ([IO.Path]::GetFullPath($ServicePath) -eq [IO.Path]::GetFullPath($Target))\n");
        script.Append("$Staged = [IO.Path]::ChangeExtension($Target, '.new.exe')\n");
        script.Append("$Old = [IO.Path]::ChangeExtension($Target, '.old.exe')\n");
        script.Append("if ($Binary -eq '') {\n");
        if (downloads)
        {
            script.Append("    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12\n");
            script.Append("    Invoke-WebRequest -UseBasicParsing -Uri \"$Origin/-/runner/download/").Append(BinaryName("win-x64")).Append("\" -OutFile $Staged\n");
        }
        else
        {
            script.Append("    throw 'This forge does not serve runner binaries. Build or copy graft-runner.exe and pass it with -Binary.'\n");
        }
        script.Append("} else { Copy-Item -LiteralPath $Binary -Destination $Staged -Force }\n");
        script.Append("$NewVersion = ''\n");
        script.Append("$Started = $false\n");
        script.Append("try { $NewVersion = (& $Staged version) -join ' '; $Started = ($LASTEXITCODE -eq 0) } catch { $NewVersion = $_.Exception.Message }\n");
        script.Append("if (-not $Started -or $NewVersion -notlike 'graft-runner *') { Remove-Item -LiteralPath $Staged -Force; throw \"The new runner does not start: $NewVersion\" }\n");
        script.Append("$OldVersion = 'an older runner'\n");
        script.Append("try { $OldVersion = (& $Target version) -join ' ' } catch { }\n");
        script.Append("if ($Managed) {\n");
        script.Append("    Stop-Service -Name 'GraftRunner'\n");
        script.Append("    (Get-Service -Name 'GraftRunner').WaitForStatus('Stopped', [TimeSpan]::FromMinutes(1))\n");
        script.Append("}\n");
        script.Append("Move-Item -LiteralPath $Target -Destination $Old -Force\n");
        script.Append("Move-Item -LiteralPath $Staged -Destination $Target -Force\n");
        script.Append("if ($Managed) {\n");
        script.Append("    ");
        AppendRecovery(script);
        script.Append("    Start-Service -Name 'GraftRunner'\n");
        script.Append("    Write-Host \"Updated $OldVersion to $NewVersion and restarted the GraftRunner service. The old executable is $Old.\"\n");
        script.Append("} else {\n");
        script.Append("    Write-Host \"Updated $Target from $OldVersion to $NewVersion. Start the runner again to use it; the old executable is $Old.\"\n");
        script.Append("}\n");
        return script.ToString();
    }

    /// <summary>
    /// The Linux update script (ADR 0058): as <see cref="UpdatePowerShell"/>, for the systemd service the install
    /// script writes. The new file is renamed over the old, so a runner still running keeps the file it started from
    /// until it is restarted.
    /// </summary>
    public static string UpdateShell(string origin, bool downloads)
    {
        StringBuilder script = new();
        script.Append("#!/bin/sh\n");
        script.Append("# Graft runner update for ").Append(origin).Append('\n');
        script.Append("# Replaces the runner's executable with the one this forge serves and restarts its service, keeping its\n");
        script.Append("# registration. Run as root. --dir names a runner installed elsewhere; --binary uses a runner you\n");
        script.Append("# already have. A job the runner is running when the service restarts is stopped with it.\n");
        script.Append("set -eu\n");
        script.Append("ORIGIN=").Append(ShellQuote(origin)).Append('\n');
        script.Append("DIR=/opt/graft-runner\n");
        script.Append("DIRSET=0\n");
        script.Append("BINARY=''\n");
        script.Append("while [ $# -gt 0 ]; do\n");
        script.Append("    case \"$1\" in\n");
        script.Append("        --dir) DIR=$2; DIRSET=1; shift 2 ;;\n");
        script.Append("        --binary) BINARY=$2; shift 2 ;;\n");
        script.Append("        *) echo \"Unknown option: $1\" >&2; exit 2 ;;\n");
        script.Append("    esac\n");
        script.Append("done\n");
        script.Append("TARGET=\"$DIR/graft-runner\"\n");
        script.Append("UNIT=/etc/systemd/system/graft-runner.service\n");
        // The service is only restarted when it runs the very executable being replaced.
        script.Append("EXEC=''\n");
        script.Append("if [ -f \"$UNIT\" ]; then EXEC=$(sed -n 's/^ExecStart=\\([^ ]*\\).*/\\1/p' \"$UNIT\" | head -n 1); fi\n");
        script.Append("if [ \"$DIRSET\" = 0 ] && [ -n \"$EXEC\" ]; then TARGET=$EXEC; fi\n");
        script.Append("if [ ! -f \"$TARGET\" ]; then\n");
        script.Append("    echo \"There is no Graft runner at $TARGET. Pass --dir, or install one with a script from the forge's Runners page.\" >&2\n");
        script.Append("    exit 1\n");
        script.Append("fi\n");
        script.Append("STAGED=\"$TARGET.new\"\n");
        script.Append("if [ -z \"$BINARY\" ]; then\n");
        if (downloads)
        {
            script.Append("    case \"$(uname -m)\" in\n");
            script.Append("        x86_64|amd64) PLATFORM=linux-x64 ;;\n");
            script.Append("        *) echo \"The Graft runner is built for x86_64 Linux; this machine is $(uname -m).\" >&2; exit 1 ;;\n");
            script.Append("    esac\n");
            script.Append("    URL=\"$ORIGIN/-/runner/download/graft-runner-$PLATFORM\"\n");
            script.Append("    if command -v curl >/dev/null 2>&1; then curl -fsSL -o \"$STAGED\" \"$URL\"\n");
            script.Append("    elif command -v wget >/dev/null 2>&1; then wget -q -O \"$STAGED\" \"$URL\"\n");
            script.Append("    else echo 'Neither curl nor wget is installed; download the runner yourself and pass it with --binary.' >&2; exit 1\n");
            script.Append("    fi\n");
        }
        else
        {
            script.Append("    echo 'This forge does not serve runner binaries. Build or copy graft-runner and pass it with --binary.' >&2\n");
            script.Append("    exit 1\n");
        }
        script.Append("else\n");
        script.Append("    cp \"$BINARY\" \"$STAGED\"\n");
        script.Append("fi\n");
        script.Append("chmod 755 \"$STAGED\"\n");
        script.Append("if ! NEW=$(\"$STAGED\" version 2>&1); then rm -f \"$STAGED\"; echo \"The new runner does not start: $NEW\" >&2; exit 1; fi\n");
        script.Append("case \"$NEW\" in\n");
        script.Append("    'graft-runner '*) ;;\n");
        script.Append("    *) rm -f \"$STAGED\"; echo \"The new runner does not start: $NEW\" >&2; exit 1 ;;\n");
        script.Append("esac\n");
        script.Append("OLD=$(\"$TARGET\" version 2>/dev/null || echo 'an older runner')\n");
        script.Append("cp -p \"$TARGET\" \"$TARGET.old\"\n");
        script.Append("mv -f \"$STAGED\" \"$TARGET\"\n");
        script.Append("if [ -n \"$EXEC\" ] && [ \"$EXEC\" = \"$TARGET\" ] && [ -d /run/systemd/system ]; then\n");
        script.Append("    systemctl restart graft-runner\n");
        script.Append("    echo \"Updated $OLD to $NEW and restarted the graft-runner service. The old executable is $TARGET.old.\"\n");
        script.Append("else\n");
        script.Append("    echo \"Updated $TARGET from $OLD to $NEW. Start the runner again to use it; the old executable is $TARGET.old.\"\n");
        script.Append("fi\n");
        return script.ToString();
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>A description in a comment line: one line, nothing that could close the comment early.</summary>
    private static string Comment(string value) =>
        new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
