#requires -Version 5.1

<#
.SYNOPSIS
Installs the public HonestFlow release-signing key on HonestLicenseServer.

.DESCRIPTION
Reads the public *.public.json created by Initialize-ReleaseSigning.ps1, writes
only the public key to a dedicated systemd EnvironmentFile, restarts the API and
checks local Swagger. The encrypted private key is never read or transferred.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublicKeyJson,
    [string]$SshTarget = "pavel-shadrov@192.168.0.103"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE." }
}

$resolvedPublicKey = (Resolve-Path -LiteralPath $PublicKeyJson).Path
$public = Get-Content -LiteralPath $resolvedPublicKey -Raw | ConvertFrom-Json
$keyId = [string]$public.keyId
$publicKeyBase64 = [string]$public.publicKeyBase64
if ($keyId -notmatch '^release_[a-f0-9]{24}$') { throw "Invalid release key ID." }
try { $publicKeyBytes = [Convert]::FromBase64String($publicKeyBase64) }
catch { throw "Public key is not valid Base64." }
if ($publicKeyBytes.Length -ne 91) { throw "Expected a P-256 SubjectPublicKeyInfo public key." }

if (-not $PSCmdlet.ShouldProcess($SshTarget,
    "Install public release-signing key $keyId and restart honestserver")) { return }

$localPayload = [IO.Path]::GetTempFileName()
$localScript = [IO.Path]::GetTempFileName()
$remoteId = [Guid]::NewGuid().ToString("N")
$remotePayload = "/tmp/honestserver-release-key-$remoteId.env"
$remoteScript = "/tmp/honestserver-release-key-$remoteId.sh"
$remotePrepared = $false
try {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $environmentName = "ReleaseSigningKeys__${keyId}__PublicKeyBase64"
    [IO.File]::WriteAllText($localPayload, "$environmentName=$publicKeyBase64`n", $utf8NoBom)
    $bash = @'
#!/usr/bin/env bash
set -euo pipefail
INPUT="$1"
ENV_FILE="/etc/honestserver/honestserver-release-signing.env"
DROPIN_DIR="/etc/systemd/system/honestserver.service.d"
DROPIN_FILE="$DROPIN_DIR/release-signing.conf"
SERVICE="honestserver"
BACKUP=""
DROPIN_BACKUP=""

cleanup() { sudo rm -f -- "$INPUT" "$0" >/dev/null 2>&1 || true; }
trap cleanup EXIT
sudo -v
sudo install -d -m 750 -o root -g root "$(dirname "$ENV_FILE")"

BACKUP="$(sudo python3 - "$INPUT" "$ENV_FILE" <<'PY'
import datetime, os, re, shutil, sys, tempfile
input_path, env_path = sys.argv[1:3]
line = open(input_path, encoding="utf-8").read().strip()
name, sep, value = line.partition("=")
if not sep or not re.fullmatch(r"ReleaseSigningKeys__release_[a-f0-9]{24}__PublicKeyBase64", name):
    raise SystemExit("Invalid release key environment name")
if not value or "\n" in value or "\r" in value:
    raise SystemExit("Invalid release public key")
original = open(env_path, encoding="utf-8").readlines() if os.path.isfile(env_path) else []
result, replaced = [], False
for raw in original:
    if raw.startswith(name + "="):
        if not replaced:
            result.append(line + "\n")
            replaced = True
    else:
        result.append(raw)
if not replaced:
    if result and result[-1].strip(): result.append("\n")
    result.append(line + "\n")
backup = ""
if os.path.isfile(env_path):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    backup = env_path + ".backup-" + stamp
    shutil.copy2(env_path, backup)
    os.chmod(backup, 0o600)
directory = os.path.dirname(env_path)
fd, temporary = tempfile.mkstemp(prefix=".release-signing.env.", dir=directory, text=True)
try:
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as target:
        target.writelines(result)
        target.flush(); os.fsync(target.fileno())
    os.chmod(temporary, 0o600)
    os.chown(temporary, 0, 0)
    os.replace(temporary, env_path)
finally:
    if os.path.exists(temporary): os.unlink(temporary)
print(backup)
PY
)"

rollback() {
    if [[ -n "$BACKUP" ]]; then sudo cp --preserve=mode,ownership,timestamps -- "$BACKUP" "$ENV_FILE";
    else sudo rm -f -- "$ENV_FILE"; fi
    if [[ -n "$DROPIN_BACKUP" ]]; then sudo cp --preserve=mode,ownership,timestamps -- "$DROPIN_BACKUP" "$DROPIN_FILE";
    else sudo rm -f -- "$DROPIN_FILE"; fi
    sudo systemctl daemon-reload
    sudo systemctl restart "$SERVICE" || true
}

sudo chmod 600 "$ENV_FILE"
sudo install -d -m 755 "$DROPIN_DIR"
if sudo test -f "$DROPIN_FILE"; then
    DROPIN_BACKUP="$DROPIN_FILE.backup-$(date -u +%Y%m%dT%H%M%SZ)"
    sudo cp --preserve=mode,ownership,timestamps -- "$DROPIN_FILE" "$DROPIN_BACKUP"
fi
printf '[Service]\nEnvironmentFile=/etc/honestserver/honestserver-release-signing.env\n' | sudo tee "$DROPIN_FILE" >/dev/null
sudo chmod 644 "$DROPIN_FILE"
sudo systemctl daemon-reload
if ! sudo systemctl restart "$SERVICE" || ! sudo systemctl is-active --quiet "$SERVICE"; then rollback; exit 1; fi
for attempt in {1..15}; do
    status="$(curl -sS -o /dev/null -w '%{http_code}' --connect-timeout 5 --max-time 15 http://127.0.0.1:5498/swagger/v1/swagger.json || true)"
    if [[ "$status" == "200" ]]; then
        printf '%s\n' "Release signing key configured; honestserver is active. Backup: $BACKUP"
        exit 0
    fi
    sleep 2
done
sudo journalctl -u "$SERVICE" -n 30 --no-pager >&2 || true
rollback
exit 1
'@
    [IO.File]::WriteAllText($localScript, $bash.Replace("`r`n", "`n").Replace("`r", "`n"), $utf8NoBom)

    Invoke-Native ssh @($SshTarget, "install -m 600 /dev/null '$remotePayload' && install -m 700 /dev/null '$remoteScript'")
    $remotePrepared = $true
    Invoke-Native scp @($localPayload, "${SshTarget}:$remotePayload")
    Invoke-Native scp @($localScript, "${SshTarget}:$remoteScript")
    Invoke-Native ssh @("-t", $SshTarget, "bash '$remoteScript' '$remotePayload'")
    Write-Host "RELEASE_SIGNING_KEY_CONFIGURED: $keyId" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $localPayload) { Remove-Item -LiteralPath $localPayload -Force }
    if (Test-Path -LiteralPath $localScript) { Remove-Item -LiteralPath $localScript -Force }
    if ($remotePrepared) { & ssh $SshTarget "rm -f -- '$remotePayload' '$remoteScript'" 2>$null | Out-Null }
    if ($publicKeyBytes) { [Array]::Clear($publicKeyBytes, 0, $publicKeyBytes.Length) }
}
