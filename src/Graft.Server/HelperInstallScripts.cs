using System.Text;

namespace Graft.Server;

/// <summary>
/// The install scripts for git-remote-graft (ADR 0043), one for Windows PowerShell and one for a POSIX shell. Each puts
/// the helper where the user's Git can find it, without administrator rights, and runs its <c>setup</c> for this forge,
/// which is one rewrite rule and the Git LFS transfer in the user's global Git configuration. Unlike a runner's scripts
/// they carry no secret, so anybody may fetch them.
/// </summary>
internal static class HelperInstallScripts
{
    /// <summary>The name the helper is served under for a platform, from the same directory as the runner binaries.</summary>
    public static string BinaryName(string platform) => "git-remote-graft-" + platform + (platform.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "");

    public static readonly string[] Platforms = ["win-x64", "linux-x64"];

    public static string PowerShell(string origin, bool downloads)
    {
        StringBuilder script = new();
        script.Append("# git-remote-graft installer for ").Append(origin).Append('\n');
        script.Append("# Installs the helper for this user and routes this forge's Git and Git LFS traffic through it, in parts no\n");
        script.Append("# larger than the forge allows. Run it as yourself, not as an administrator:\n");
        script.Append("#   & ([scriptblock]::Create((Invoke-RestMethod ").Append(origin).Append("/-/git-remote-graft/install.ps1)))\n");
        script.Append("# -PushOnly sends only pushes through it; -Binary uses a helper you already have.\n");
        script.Append("param(\n");
        script.Append("    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\\git-remote-graft'),\n");
        script.Append("    [string]$Binary = '',\n");
        script.Append("    [switch]$PushOnly\n");
        script.Append(")\n");
        script.Append("$ErrorActionPreference = 'Stop'\n");
        script.Append("$Origin = ").Append(PowerShellQuote(origin)).Append('\n');
        script.Append("if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is not installed or not on PATH.' }\n");
        script.Append("New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null\n");
        script.Append("$Target = Join-Path $InstallDir 'git-remote-graft.exe'\n");
        script.Append("if ($Binary -ne '') { Copy-Item -Force -Path $Binary -Destination $Target }\n");
        script.Append("else {\n");
        if (downloads)
        {
            script.Append("    if (-not [Environment]::Is64BitOperatingSystem) { throw 'git-remote-graft needs 64-bit Windows.' }\n");
            script.Append("    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12\n");
            script.Append("    Invoke-WebRequest -UseBasicParsing -Uri \"$Origin/-/git-remote-graft/download/").Append(BinaryName("win-x64")).Append("\" -OutFile $Target\n");
        }
        else
        {
            script.Append("    throw 'This forge does not serve git-remote-graft. Build or copy git-remote-graft.exe and pass it with -Binary.'\n");
        }
        script.Append("}\n");
        script.Append("# Git finds a remote helper on PATH, so the directory goes on this user's PATH, for new terminals and this one.\n");
        script.Append("$UserPath = [Environment]::GetEnvironmentVariable('Path', 'User')\n");
        script.Append("if (-not (($UserPath -split ';') -contains $InstallDir)) {\n");
        script.Append("    [Environment]::SetEnvironmentVariable('Path', (($UserPath, $InstallDir) | Where-Object { $_ }) -join ';', 'User')\n");
        script.Append("    Write-Host \"Added $InstallDir to your PATH. Terminals and editors opened before now need restarting to see it.\"\n");
        script.Append("}\n");
        script.Append("if (-not (($env:Path -split ';') -contains $InstallDir)) { $env:Path = $env:Path + ';' + $InstallDir }\n");
        script.Append("if ($PushOnly) { & $Target setup $Origin --push-only } else { & $Target setup $Origin }\n");
        script.Append("if ($LASTEXITCODE -ne 0) { throw \"git-remote-graft setup failed (exit $LASTEXITCODE).\" }\n");
        return script.ToString();
    }

    public static string Shell(string origin, bool downloads)
    {
        StringBuilder script = new();
        script.Append("#!/bin/sh\n");
        script.Append("# git-remote-graft installer for ").Append(origin).Append('\n');
        script.Append("# Installs the helper for this user and routes this forge's Git and Git LFS traffic through it, in parts no\n");
        script.Append("# larger than the forge allows. Run it as yourself, not as root:\n");
        script.Append("#   curl -fsSL ").Append(origin).Append("/-/git-remote-graft/install.sh | sh\n");
        script.Append("# --push-only sends only pushes through it; --binary uses a helper you already have; --dir changes where it goes.\n");
        script.Append("set -eu\n");
        script.Append("ORIGIN=").Append(ShellQuote(origin)).Append('\n');
        script.Append("DIR=\"$HOME/.local/bin\"\n");
        script.Append("BINARY=''\n");
        script.Append("MODE=''\n");
        script.Append("while [ $# -gt 0 ]; do\n");
        script.Append("    case \"$1\" in\n");
        script.Append("        --dir) DIR=$2; shift 2 ;;\n");
        script.Append("        --binary) BINARY=$2; shift 2 ;;\n");
        script.Append("        --push-only) MODE=--push-only; shift ;;\n");
        script.Append("        *) echo \"Unknown option: $1\" >&2; exit 2 ;;\n");
        script.Append("    esac\n");
        script.Append("done\n");
        script.Append("command -v git >/dev/null 2>&1 || { echo 'Git is not installed or not on PATH.' >&2; exit 1; }\n");
        script.Append("mkdir -p \"$DIR\"\n");
        script.Append("TARGET=\"$DIR/git-remote-graft\"\n");
        script.Append("if [ -n \"$BINARY\" ]; then cp \"$BINARY\" \"$TARGET\"\n");
        script.Append("else\n");
        if (downloads)
        {
            script.Append("    case \"$(uname -s)-$(uname -m)\" in\n");
            script.Append("        Linux-x86_64|Linux-amd64) PLATFORM=linux-x64 ;;\n");
            script.Append("        *) echo \"git-remote-graft is built for x86_64 Linux; this machine is $(uname -s) $(uname -m).\" >&2; exit 1 ;;\n");
            script.Append("    esac\n");
            script.Append("    URL=\"$ORIGIN/-/git-remote-graft/download/git-remote-graft-$PLATFORM\"\n");
            script.Append("    if command -v curl >/dev/null 2>&1; then curl -fsSL -o \"$TARGET\" \"$URL\"\n");
            script.Append("    elif command -v wget >/dev/null 2>&1; then wget -q -O \"$TARGET\" \"$URL\"\n");
            script.Append("    else echo 'Neither curl nor wget is installed; download git-remote-graft yourself and pass it with --binary.' >&2; exit 1\n");
            script.Append("    fi\n");
        }
        else
        {
            script.Append("    echo 'This forge does not serve git-remote-graft. Build or copy it and pass it with --binary.' >&2\n");
            script.Append("    exit 1\n");
        }
        script.Append("fi\n");
        script.Append("chmod 755 \"$TARGET\"\n");
        script.Append("case \":$PATH:\" in\n");
        script.Append("    *\":$DIR:\"*) ;;\n");
        script.Append("    *) echo \"Add $DIR to PATH in your shell's profile: Git finds the helper there.\" ;;\n");
        script.Append("esac\n");
        script.Append("\"$TARGET\" setup \"$ORIGIN\" $MODE\n");
        return script.ToString();
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
