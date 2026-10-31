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
        return script.ToString();
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>A description in a comment line: one line, nothing that could close the comment early.</summary>
    private static string Comment(string value) =>
        new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
