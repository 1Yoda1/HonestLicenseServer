#requires -Version 5.1

<#
.SYNOPSIS
Publishes the static HonestFlow website to the existing Caddy web root.

.DESCRIPTION
Creates a local archive of site/, makes a timestamped server backup, and extracts
the new files into /opt/honestserver/site. Files not owned by this source tree,
including an existing yandex_*.html verification file, are intentionally retained.
No API code, database, secrets, or Caddy configuration is changed.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [string]$SiteDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "site"),
    [string]$SshTarget = "pavel-shadrov@192.168.0.103",
    [string]$WebRoot = "/opt/honestserver/site"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command was not found: $Name"
    }
}

function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE." }
}

$resolvedSite = (Resolve-Path -LiteralPath $SiteDirectory).Path
foreach ($requiredFile in @("index.html", "styles.css", "app.js", "site-config.js", "honestflow/index.html")) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedSite $requiredFile) -PathType Leaf)) {
        throw "Required site file was not found: $requiredFile"
    }
}

Assert-Command "tar.exe"
Assert-Command "scp"
Assert-Command "ssh"

if (-not $PSCmdlet.ShouldProcess($SshTarget, "Publish static website to $WebRoot")) { return }

$localArchive = Join-Path ([IO.Path]::GetTempPath()) ("honestflow-site-" + [Guid]::NewGuid().ToString("N") + ".tar.gz")
$localScript = Join-Path ([IO.Path]::GetTempPath()) ("honestflow-site-" + [Guid]::NewGuid().ToString("N") + ".sh")
$remoteId = [Guid]::NewGuid().ToString("N")
$remoteArchive = "/tmp/honestflow-site-$remoteId.tar.gz"
$remoteScript = "/tmp/honestflow-site-$remoteId.sh"

try {
    Invoke-Native "tar.exe" @("-czf", $localArchive, "-C", $resolvedSite, ".")

    $bash = @'
#!/usr/bin/env bash
set -euo pipefail

ARCHIVE="$1"
WEB_ROOT="$2"
BACKUP_ROOT="/opt/honestserver/backups"

cleanup() { rm -f -- "$ARCHIVE" "$0"; }
trap cleanup EXIT

tar -tzf "$ARCHIVE" | grep -E '(^|\./)index\.html$' >/dev/null
tar -tzf "$ARCHIVE" | grep -E '(^|\./)honestflow/index\.html$' >/dev/null

sudo -v
sudo install -d -m 755 -o root -g root "$WEB_ROOT" "$BACKUP_ROOT"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
sudo tar -czf "$BACKUP_ROOT/site-before-$STAMP.tar.gz" -C "$WEB_ROOT" .
sudo tar -xzf "$ARCHIVE" -C "$WEB_ROOT"
sudo chown -R root:caddy "$WEB_ROOT"
sudo find "$WEB_ROOT" -type d -exec chmod 755 {} +
sudo find "$WEB_ROOT" -type f -exec chmod 644 {} +

curl -fsS --max-time 20 https://honestflow.ru/ >/dev/null
curl -fsS --max-time 20 https://honestflow.ru/honestflow/ >/dev/null
echo "SITE_PUBLISHED: https://honestflow.ru/honestflow/"
echo "BACKUP: $BACKUP_ROOT/site-before-$STAMP.tar.gz"
'@
    [IO.File]::WriteAllText($localScript, $bash, (New-Object Text.UTF8Encoding($false)))

    Write-Host "[1/3] Uploading website archive..."
    Invoke-Native "scp" @($localArchive, "${SshTarget}:$remoteArchive")
    Invoke-Native "scp" @($localScript, "${SshTarget}:$remoteScript")
    Write-Host "[2/3] Publishing files and retaining unrelated verification files..."
    Invoke-Native "ssh" @("-tt", $SshTarget, "bash", $remoteScript, $remoteArchive, $WebRoot)
    Write-Host "[3/3] Website publication completed."
}
finally {
    Remove-Item -LiteralPath $localArchive, $localScript -Force -ErrorAction SilentlyContinue
}
