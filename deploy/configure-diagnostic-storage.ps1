#requires -Version 5.1

<#
.SYNOPSIS
Securely configures Cloud.ru S3 credentials for diagnostic archives.

.DESCRIPTION
Prompts for the access key and secret key without echoing them, uploads a
temporary mode-600 file, writes only DiagnosticStorage__* values to a dedicated
systemd EnvironmentFile, restarts honestserver and checks the local Swagger
endpoint. The production database is never read or changed.

.EXAMPLE
.\deploy\configure-diagnostic-storage.ps1

.EXAMPLE
.\deploy\configure-diagnostic-storage.ps1 -SshTarget user@server
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [string]$SshTarget = "pavel-shadrov@192.168.0.103",
    [string]$TenantId,
    [string]$ServiceUrl = "https://s3.cloud.ru",
    [string]$Region = "ru-central-1",
    [string]$Bucket = "bucket-honestflow",
    [ValidateRange(1, 168)]
    [int]$RetentionHours = 48,
    [ValidateRange(5, 60)]
    [int]$UploadUrlMinutes = 15,
    [ValidateRange(2, 60)]
    [int]$DownloadUrlMinutes = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Command {
    param([Parameter(Mandatory = $true)][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command was not found: $Name"
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function ConvertTo-PlainText {
    param([Parameter(Mandatory = $true)][Security.SecureString]$Value)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Assert-SingleLineSecret {
    param([Parameter(Mandatory = $true)][string]$Name, [string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must not be empty."
    }
    if ($Value.Contains("`r") -or $Value.Contains("`n") -or $Value.IndexOf([char]0) -ge 0) {
        throw "$Name must contain one line only."
    }
    if ($Value -notmatch '^[A-Za-z0-9._~+/=-]+$') {
        throw "$Name contains characters that are unsafe for a systemd EnvironmentFile."
    }
}

Assert-Command "ssh"
Assert-Command "scp"

if ([string]::IsNullOrWhiteSpace($TenantId)) {
    $TenantId = Read-Host "Cloud.ru Tenant ID (из Object Storage → Параметры работы с API)"
}
$accessKeySecure = Read-Host "Cloud.ru Key ID (ввод скрыт)" -AsSecureString
$secretKeySecure = Read-Host "Cloud.ru Key Secret (ввод скрыт)" -AsSecureString
$keyId = ConvertTo-PlainText $accessKeySecure
$accessKey = "$TenantId`:$keyId"
$secretKey = ConvertTo-PlainText $secretKeySecure

$localCredentials = $null
$localRemoteScript = $null
$remoteFilesPrepared = $false
$remoteId = [Guid]::NewGuid().ToString("N")
$remoteCredentials = "/tmp/honestserver-diagnostics-$remoteId.env"
$remoteScript = "/tmp/honestserver-diagnostics-$remoteId.sh"

try {
    Assert-SingleLineSecret "Cloud.ru Tenant ID" $TenantId
    Assert-SingleLineSecret "Cloud.ru Key ID" $keyId
    Assert-SingleLineSecret "Cloud.ru Key Secret" $secretKey
    foreach ($value in @($ServiceUrl, $Region, $Bucket)) {
        if ([string]::IsNullOrWhiteSpace($value) -or $value.Contains("`r") -or $value.Contains("`n")) {
            throw "Storage settings must be non-empty single-line values."
        }
    }

    if (-not $PSCmdlet.ShouldProcess(
        $SshTarget,
        "Update Cloud.ru diagnostic storage credentials and restart honestserver")) {
        Write-Host "Cancelled."
        return
    }

    $localCredentials = [IO.Path]::GetTempFileName()
    $localRemoteScript = [IO.Path]::GetTempFileName()
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    $settings = @(
        "DiagnosticStorage__ServiceUrl=$ServiceUrl"
        "DiagnosticStorage__Region=$Region"
        "DiagnosticStorage__Bucket=$Bucket"
        "DiagnosticStorage__AccessKey=$accessKey"
        "DiagnosticStorage__SecretKey=$secretKey"
        "DiagnosticStorage__RetentionHours=$RetentionHours"
        "DiagnosticStorage__UploadUrlMinutes=$UploadUrlMinutes"
        "DiagnosticStorage__DownloadUrlMinutes=$DownloadUrlMinutes"
    ) -join "`n"
    [IO.File]::WriteAllText($localCredentials, $settings + "`n", $utf8NoBom)

    $bash = @'
#!/usr/bin/env bash
set -euo pipefail

INPUT="$1"
ENV_FILE="/etc/honestserver/honestserver-diagnostics.env"
DROPIN_DIR="/etc/systemd/system/honestserver.service.d"
DROPIN_FILE="$DROPIN_DIR/diagnostic-storage.conf"
SERVICE="honestserver"
LOCAL_SWAGGER="http://127.0.0.1:5498/swagger/v1/swagger.json"
DROPIN_BACKUP=""

cleanup() {
    sudo rm -f -- "$INPUT" "$0" >/dev/null 2>&1 || true
}
trap cleanup EXIT

sudo -v
sudo install -d -m 750 -o root -g root "$(dirname "$ENV_FILE")"
BACKUP_PATH="$(sudo python3 - "$INPUT" "$ENV_FILE" <<'PY'
import datetime
import os
import shutil
import sys
import tempfile

input_path, env_path = sys.argv[1:3]
expected = [
    "DiagnosticStorage__ServiceUrl",
    "DiagnosticStorage__Region",
    "DiagnosticStorage__Bucket",
    "DiagnosticStorage__AccessKey",
    "DiagnosticStorage__SecretKey",
    "DiagnosticStorage__RetentionHours",
    "DiagnosticStorage__UploadUrlMinutes",
    "DiagnosticStorage__DownloadUrlMinutes",
]

updates = {}
with open(input_path, "r", encoding="utf-8") as source:
    for raw in source:
        line = raw.rstrip("\n")
        if not line:
            continue
        key, separator, value = line.partition("=")
        if not separator or key not in expected or "\n" in value or "\r" in value:
            raise SystemExit("Temporary credential file has an invalid entry")
        updates[key] = value

if set(updates) != set(expected) or not updates["DiagnosticStorage__AccessKey"] or not updates["DiagnosticStorage__SecretKey"]:
    raise SystemExit("Temporary credential file is incomplete")

exists = os.path.isfile(env_path)
if exists:
    with open(env_path, "r", encoding="utf-8") as source:
        original = source.readlines()
    stat = os.stat(env_path)
else:
    original = []
    stat = None

result = []
written = set()
for raw in original:
    stripped = raw.lstrip()
    matched = next((key for key in expected if stripped.startswith(key + "=")), None)
    if matched is None:
        result.append(raw)
    elif matched not in written:
        result.append(f"{matched}={updates[matched]}\n")
        written.add(matched)

missing = [key for key in expected if key not in written]
if missing:
    if result and not result[-1].endswith("\n"):
        result[-1] += "\n"
    if result and result[-1].strip():
        result.append("\n")
    result.append("# Cloud.ru S3 storage for diagnostic archives.\n")
    result.extend(f"{key}={updates[key]}\n" for key in missing)

timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
backup_path = ""
if exists:
    backup_path = f"{env_path}.backup-{timestamp}"
    shutil.copy2(env_path, backup_path)
    os.chmod(backup_path, 0o600)

directory = os.path.dirname(env_path)
descriptor, temporary_path = tempfile.mkstemp(prefix=".honestserver.env.", dir=directory, text=True)
try:
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as target:
        target.writelines(result)
        target.flush()
        os.fsync(target.fileno())
    os.chmod(temporary_path, 0o600)
    if stat is not None:
        os.chown(temporary_path, stat.st_uid, stat.st_gid)
    else:
        os.chown(temporary_path, 0, 0)
    os.replace(temporary_path, env_path)
finally:
    if os.path.exists(temporary_path):
        os.unlink(temporary_path)

print(backup_path)
PY
)"

rollback_environment() {
    if [[ -n "$BACKUP_PATH" ]]; then
        printf '%s\n' "Restoring diagnostic environment from $BACKUP_PATH" >&2
        sudo cp --preserve=mode,ownership,timestamps -- "$BACKUP_PATH" "$ENV_FILE"
        sudo chmod 600 "$ENV_FILE"
    else
        sudo rm -f -- "$ENV_FILE"
    fi
    if [[ -n "$DROPIN_BACKUP" ]]; then
        sudo cp --preserve=mode,ownership,timestamps -- "$DROPIN_BACKUP" "$DROPIN_FILE"
    else
        sudo rm -f -- "$DROPIN_FILE"
    fi
    sudo systemctl daemon-reload
    sudo systemctl restart "$SERVICE" || true
    if sudo systemctl is-active --quiet "$SERVICE"; then
        printf '%s\n' "Previous environment restored; honestserver is active." >&2
    else
        printf '%s\n' "CRITICAL: previous environment was restored, but honestserver is not active." >&2
    fi
}

printf '%s\n' "Environment updated. Backup: $BACKUP_PATH"

sudo chmod 600 "$ENV_FILE"
sudo install -d -m 755 "$DROPIN_DIR"
if sudo test -f "$DROPIN_FILE"; then
    DROPIN_BACKUP="$DROPIN_FILE.backup-$(date -u +%Y%m%dT%H%M%SZ)"
    sudo cp --preserve=mode,ownership,timestamps -- "$DROPIN_FILE" "$DROPIN_BACKUP"
fi
sudo tee "$DROPIN_FILE" >/dev/null <<'EOF'
[Service]
EnvironmentFile=/etc/honestserver/honestserver-diagnostics.env
EOF
sudo chmod 644 "$DROPIN_FILE"
sudo systemctl daemon-reload
if ! sudo systemctl restart "$SERVICE" || ! sudo systemctl is-active --quiet "$SERVICE"; then
    rollback_environment
    exit 1
fi

status="000"
for attempt in {1..15}; do
    status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
        --connect-timeout 5 --max-time 15 "$LOCAL_SWAGGER")" || status="000"
    if [[ "$status" == "200" ]]; then
        printf '%s\n' "Diagnostic storage configured; honestserver is active; local Swagger HTTP 200."
        exit 0
    fi
    sleep 2
done

printf 'Local Swagger health check failed with HTTP %s.\n' "$status" >&2
sudo journalctl -u "$SERVICE" -n 30 --no-pager >&2 || true
rollback_environment
exit 1
'@
    $normalizedBash = $bash.Replace("`r`n", "`n").Replace("`r", "`n")
    [IO.File]::WriteAllText($localRemoteScript, $normalizedBash, $utf8NoBom)

    Write-Host "[1/4] Preparing protected temporary files on server..." -ForegroundColor Cyan
    Invoke-Native -FilePath "ssh" -Arguments @(
        $SshTarget,
        "install -m 600 /dev/null '$remoteCredentials' && install -m 700 /dev/null '$remoteScript'")
    $remoteFilesPrepared = $true

    Write-Host "[2/4] Uploading configuration payload..." -ForegroundColor Cyan
    Invoke-Native -FilePath "scp" -Arguments @($localCredentials, "${SshTarget}:$remoteCredentials")
    Invoke-Native -FilePath "scp" -Arguments @($localRemoteScript, "${SshTarget}:$remoteScript")

    Write-Host "[3/4] Updating environment and restarting service..." -ForegroundColor Cyan
    Invoke-Native -FilePath "ssh" -Arguments @(
        "-t", $SshTarget, "bash '$remoteScript' '$remoteCredentials'")

    Write-Host "[4/4] CONFIGURATION_OK" -ForegroundColor Green
}
finally {
    $TenantId = $null
    $keyId = $null
    $accessKey = $null
    $secretKey = $null
    $settings = $null
    if ($localCredentials -and (Test-Path -LiteralPath $localCredentials)) {
        Remove-Item -LiteralPath $localCredentials -Force
    }
    if ($localRemoteScript -and (Test-Path -LiteralPath $localRemoteScript)) {
        Remove-Item -LiteralPath $localRemoteScript -Force
    }
    if ($remoteFilesPrepared) {
        & ssh $SshTarget "rm -f -- '$remoteCredentials' '$remoteScript'" 2>$null | Out-Null
    }
}
