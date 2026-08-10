#requires -Version 5.1

<#
.SYNOPSIS
Safely deploys HonestLicenseServer code or a code-plus-database-schema change.

.EXAMPLE
.\deploy-production.ps1 -Mode Code

.EXAMPLE
.\deploy-production.ps1 -Mode Database `
    -ExpectedColumn "DeviceRegistrationRequests.RequestedHonestFlowVersion"

.EXAMPLE
.\deploy-production.ps1 -Mode Database `
    -ExpectedColumn "DeviceRegistrationRequests.RequestedHonestFlowVersion" `
    -ValidateOnly
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Code", "Database")]
    [string]$Mode,

    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$ExpectedColumn,

    [string]$SshTarget = "pavel-shadrov@192.168.0.103",

    [switch]$ValidateOnly,

    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repository = $PSScriptRoot
$project = Join-Path $repository "HonestLicenseServer.csproj"
$publishDirectory = Join-Path $repository "publish-linux"
$binary = Join-Path $publishDirectory "HonestLicenseServer"
$generatedBash = Join-Path $publishDirectory "deploy.sh"

$remoteRoot = "/opt/honestserver"
$remoteStaging = "$remoteRoot/.deploy"
$remoteBinary = "$remoteStaging/HonestLicenseServer.upload"
$remoteDeployScript = "$remoteStaging/deploy.sh"

function Write-Step {
    param([int]$Number, [string]$Text)
    Write-Host "[$Number/8] $Text" -ForegroundColor Cyan
}

function Assert-Command {
    param([string]$Name)
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

function Get-LaunchedFromExplorer {
    try {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $PID"
        return $process.CommandLine -match '(?i)(-File|-Command)' -and
            $process.CommandLine -match [Regex]::Escape((Split-Path -Leaf $PSCommandPath))
    }
    catch {
        return $false
    }
}

function Get-ElfMagic {
    param([string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $magic = New-Object byte[] 4
        if ($stream.Read($magic, 0, 4) -ne 4) {
            throw "Published binary is shorter than four bytes."
        }
        return ($magic | ForEach-Object { $_.ToString("X2") }) -join " "
    }
    finally {
        $stream.Dispose()
    }
}

function Write-Utf8NoBomLf {
    param([string]$Path, [string]$Content)

    $normalized = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $normalized, $encoding)
}

function New-RemoteDeployScript {
    param(
        [string]$DeployMode,
        [string]$ExpectedSha256,
        [string]$ExpectedTable,
        [string]$ExpectedColumnName
    )

    $bash = @'
#!/usr/bin/env bash
set -u
set -o pipefail

MODE="__MODE__"
EXPECTED_SHA256="__EXPECTED_SHA256__"
EXPECTED_TABLE="__EXPECTED_TABLE__"
EXPECTED_COLUMN="__EXPECTED_COLUMN__"

APP_USER="pavel-shadrov"
SERVICE="honestserver"
ROOT="/opt/honestserver"
STAGING="$ROOT/.deploy"
BACKUPS="$ROOT/backups"
PRODUCTION_BINARY="$ROOT/HonestLicenseServer"
UPLOAD_BINARY="$STAGING/HonestLicenseServer.upload"
DATABASE="$ROOT/HonestLicenseFull.db"
LOCAL_SWAGGER="http://127.0.0.1:5498/swagger/v1/swagger.json"
PUBLIC_SWAGGER="https://api.honestflow.ru/swagger/v1/swagger.json"
HTTP_STATUS="000"
FAIL_REASON=""

log() {
    printf '%s\n' "$*"
}

die() {
    printf 'DEPLOY ERROR\n%s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "Required command is missing on the server: $1"
}

sha256_file() {
    sha256sum "$1" | awk '{print $1}'
}

production_sha_matches() {
    local actual_sha
    actual_sha="$(sha256_file "$PRODUCTION_BINARY")" || return 1
    log "Production SHA256: $actual_sha"
    [[ "$actual_sha" == "$EXPECTED_SHA256" ]]
}

verify_elf() {
    local path="$1"
    local description
    description="$(file -b "$path")" || return 1
    log "Uploaded file: $description"
    case "$description" in
        ELF\ 64-bit*) return 0 ;;
        *) return 1 ;;
    esac
}

probe_http_200() {
    local url="$1"
    HTTP_STATUS="$(curl --silent --show-error --location --output /dev/null \
        --write-out '%{http_code}' --connect-timeout 5 --max-time 15 "$url")" || HTTP_STATUS="000"
    [[ "$HTTP_STATUS" == "200" ]]
}

wait_http_200() {
    local url="$1"
    local attempts="$2"
    local delay="$3"
    local attempt
    for ((attempt = 1; attempt <= attempts; attempt++)); do
        if probe_http_200 "$url"; then
            log "HTTP 200: $url"
            return 0
        fi
        sleep "$delay"
    done
    log "HTTP $HTTP_STATUS: $url"
    return 1
}

service_is_active() {
    sudo systemctl is-active --quiet "$SERVICE"
}

sqlite_backup() {
    local source="$1"
    local destination="$2"
    sudo -u "$APP_USER" python3 - "$source" "$destination" <<'PY'
import sqlite3
import sys

source_path, destination_path = sys.argv[1], sys.argv[2]
source = sqlite3.connect(f"file:{source_path}?mode=ro", uri=True)
destination = sqlite3.connect(destination_path)
try:
    source.backup(destination)
    result = destination.execute("PRAGMA integrity_check").fetchone()[0]
    if result != "ok":
        raise RuntimeError(f"backup integrity_check returned: {result}")
finally:
    destination.close()
    source.close()
PY
}

sqlite_integrity_ok() {
    local database_path="$1"
    local result
    result="$(sudo -u "$APP_USER" python3 - "$database_path" <<'PY'
import sqlite3
import sys

connection = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
try:
    print(connection.execute("PRAGMA integrity_check").fetchone()[0])
finally:
    connection.close()
PY
)" || return 1
    log "SQLite integrity_check: $result"
    [[ "$result" == "ok" ]]
}

schema_column_exists() {
    local database_path="$1"
    local table="$2"
    local column="$3"
    sudo -u "$APP_USER" python3 - "$database_path" "$table" "$column" <<'PY'
import sqlite3
import sys

database_path, table, expected_column = sys.argv[1:4]
if not table.replace("_", "a").isalnum() or not expected_column.replace("_", "a").isalnum():
    raise SystemExit("Unsafe table or column identifier")
connection = sqlite3.connect(f"file:{database_path}?mode=ro", uri=True)
try:
    columns = {row[1] for row in connection.execute(f'PRAGMA table_info("{table}")')}
finally:
    connection.close()
if expected_column not in columns:
    raise SystemExit(f"Missing expected column: {table}.{expected_column}")
print(f"Schema column exists: {table}.{expected_column}")
PY
}

verify_common_preconditions() {
    local command_name
    for command_name in sudo systemctl install sha256sum file curl python3 date id awk; do
        require_command "$command_name"
    done

    [[ "$MODE" == "CODE" || "$MODE" == "DATABASE" ]] || die "Unsupported mode: $MODE"
    [[ -f "$UPLOAD_BINARY" ]] || die "Uploaded binary was not found: $UPLOAD_BINARY"
    [[ -f "$PRODUCTION_BINARY" ]] || die "Production binary was not found: $PRODUCTION_BINARY"
    [[ -d "$STAGING" && -w "$STAGING" ]] || die "Staging directory is unavailable or not writable: $STAGING"
    verify_elf "$UPLOAD_BINARY" || die "Uploaded file is not a Linux x64 ELF binary."

    local uploaded_sha
    uploaded_sha="$(sha256_file "$UPLOAD_BINARY")" || die "Unable to calculate uploaded SHA256."
    [[ "$uploaded_sha" == "$EXPECTED_SHA256" ]] || \
        die "Uploaded SHA256 mismatch. Expected $EXPECTED_SHA256, got $uploaded_sha."
    log "Uploaded SHA256: $uploaded_sha"

    sudo -v || die "sudo authentication failed."
    python3 -c 'import sqlite3' || die "Python sqlite3 module is unavailable."
    service_is_active || die "Service $SERVICE must be active before deployment."
    wait_http_200 "$LOCAL_SWAGGER" 3 2 || die "Current local API is not healthy; refusing to stop it."
    if probe_http_200 "$PUBLIC_SWAGGER"; then
        log "Current public API HTTP 200."
    else
        log "WARNING: current public API returned HTTP $HTTP_STATUS."
    fi
}

prepare_files() {
    local backup_kind="$1"
    TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
    BACKUP_DIR="$BACKUPS/${backup_kind}-${TIMESTAMP}"
    BACKUP_BINARY="$BACKUP_DIR/HonestLicenseServer"
    PREPARED_BINARY="$STAGING/HonestLicenseServer.prepared-${TIMESTAMP}"
    APP_GROUP="$(id -gn "$APP_USER")" || die "Unable to resolve group for $APP_USER."
    OLD_SHA256="$(sha256_file "$PRODUCTION_BINARY")" || die "Unable to calculate current binary SHA256."

    [[ ! -e "$BACKUP_DIR" ]] || die "Backup directory already exists: $BACKUP_DIR"
    sudo install -d -m 775 -o "$APP_USER" -g "$APP_GROUP" "$BACKUP_DIR" || \
        die "Unable to create backup directory."
    sudo install -m 775 -o "$APP_USER" -g "$APP_GROUP" \
        "$PRODUCTION_BINARY" "$BACKUP_BINARY" || die "Unable to back up current binary."
    sudo install -m 775 -o "$APP_USER" -g "$APP_GROUP" \
        "$UPLOAD_BINARY" "$PREPARED_BINARY" || die "Unable to prepare new binary."

    local prepared_sha
    prepared_sha="$(sha256_file "$PREPARED_BINARY")" || die "Unable to hash prepared binary."
    [[ "$prepared_sha" == "$EXPECTED_SHA256" ]] || die "Prepared binary SHA256 mismatch."
    log "Backup directory: $BACKUP_DIR"
}

stop_service() {
    sudo systemctl stop "$SERVICE" || true
    if service_is_active; then
        return 1
    fi
    return 0
}

install_prepared_binary() {
    sudo mv -f "$PREPARED_BINARY" "$PRODUCTION_BINARY" || return 1
    sudo chown "$APP_USER:$APP_GROUP" "$PRODUCTION_BINARY" || return 1
    sudo chmod 775 "$PRODUCTION_BINARY" || return 1
}

rollback_code() {
    local failed=0
    log "Starting code rollback."
    sudo systemctl stop "$SERVICE" || true
    if service_is_active; then
        log "CRITICAL_ROLLBACK_NEEDS_MANUAL_CHECK"
        return 1
    fi
    sudo install -m 775 -o "$APP_USER" -g "$APP_GROUP" \
        "$BACKUP_BINARY" "$PREPARED_BINARY.rollback" || failed=1
    if [[ "$failed" -eq 0 ]]; then
        sudo mv -f "$PREPARED_BINARY.rollback" "$PRODUCTION_BINARY" || failed=1
    fi
    sudo systemctl start "$SERVICE" || true
    if [[ "$failed" -eq 0 ]] && service_is_active && \
        wait_http_200 "$LOCAL_SWAGGER" 15 2 && \
        [[ "$(sha256_file "$PRODUCTION_BINARY")" == "$OLD_SHA256" ]]; then
        if probe_http_200 "$PUBLIC_SWAGGER"; then
            log "Rollback public API HTTP 200."
        else
            log "WARNING: rollback public API returned HTTP $HTTP_STATUS."
        fi
        log "ROLLBACK_OK"
        return 0
    fi
    log "CRITICAL_ROLLBACK_NEEDS_MANUAL_CHECK"
    return 1
}

deploy_code() {
    log "[remote 1/5] Preflight"
    verify_common_preconditions
    log "[remote 2/5] Backup and prepare"
    prepare_files "code"
    log "[remote 3/5] Stop and atomically replace"
    stop_service || die "Unable to stop $SERVICE; production binary was not replaced."
    if ! install_prepared_binary; then
        FAIL_REASON="Unable to atomically install the new binary."
        rollback_code || true
        die "$FAIL_REASON"
    fi
    sudo systemctl start "$SERVICE" || FAIL_REASON="Unable to start $SERVICE with the new binary."

    log "[remote 4/5] Health checks"
    if [[ -z "$FAIL_REASON" ]] && ! service_is_active; then
        FAIL_REASON="Service is not active after code deployment."
    fi
    if [[ -z "$FAIL_REASON" ]] && ! wait_http_200 "$LOCAL_SWAGGER" 15 2; then
        FAIL_REASON="Local Swagger did not return HTTP 200."
    fi
    if [[ -z "$FAIL_REASON" ]] && ! production_sha_matches; then
        FAIL_REASON="Production binary SHA256 does not match the uploaded binary."
    fi
    if [[ -z "$FAIL_REASON" ]] && ! wait_http_200 "$PUBLIC_SWAGGER" 5 3; then
        FAIL_REASON="Public Swagger did not return HTTP 200."
    fi

    if [[ -n "$FAIL_REASON" ]]; then
        rollback_code || true
        die "$FAIL_REASON"
    fi
    log "[remote 5/5] DEPLOY_OK"
}

restart_old_after_backup_failure() {
    sudo systemctl start "$SERVICE" || true
    if service_is_active && wait_http_200 "$LOCAL_SWAGGER" 15 2; then
        log "Old service restarted after failed database backup."
        return 0
    fi
    log "CRITICAL_ROLLBACK_NEEDS_MANUAL_CHECK"
    return 1
}

rollback_database() {
    local database_backup="$1"
    local failed=0
    log "Starting full binary and database rollback."
    sudo systemctl stop "$SERVICE" || true
    if service_is_active; then
        log "CRITICAL_ROLLBACK_NEEDS_MANUAL_CHECK"
        return 1
    fi
    sudo rm -f -- "$DATABASE" "${DATABASE}-wal" "${DATABASE}-shm" || failed=1
    sudo install -m 660 -o "$APP_USER" -g "$APP_GROUP" \
        "$database_backup" "$DATABASE" || failed=1
    sudo install -m 775 -o "$APP_USER" -g "$APP_GROUP" \
        "$BACKUP_BINARY" "$PREPARED_BINARY.rollback" || failed=1
    if [[ "$failed" -eq 0 ]]; then
        sudo mv -f "$PREPARED_BINARY.rollback" "$PRODUCTION_BINARY" || failed=1
    fi
    sudo systemctl start "$SERVICE" || true

    if [[ "$failed" -eq 0 ]] && service_is_active && \
        wait_http_200 "$LOCAL_SWAGGER" 15 2 && \
        sqlite_integrity_ok "$DATABASE" && \
        [[ "$(sha256_file "$PRODUCTION_BINARY")" == "$OLD_SHA256" ]]; then
        if probe_http_200 "$PUBLIC_SWAGGER"; then
            log "Rollback public API HTTP 200."
        else
            log "WARNING: rollback public API returned HTTP $HTTP_STATUS."
        fi
        log "ROLLBACK_OK"
        return 0
    fi
    log "CRITICAL_ROLLBACK_NEEDS_MANUAL_CHECK"
    return 1
}

deploy_database() {
    log "[remote 1/7] Preflight"
    verify_common_preconditions
    [[ -f "$DATABASE" ]] || die "Production database was not found: $DATABASE"

    log "[remote 2/7] Backup directory and binaries"
    prepare_files "database"
    DATABASE_BACKUP="$BACKUP_DIR/HonestLicenseFull.db"

    log "[remote 3/7] Stop service"
    stop_service || die "Unable to stop $SERVICE; SQLite backup was not started."

    log "[remote 4/7] Consistent SQLite backup"
    if ! sqlite_backup "$DATABASE" "$DATABASE_BACKUP"; then
        restart_old_after_backup_failure || true
        die "SQLite backup or backup integrity_check failed; production binary and database were not changed."
    fi
    sudo chmod 660 "$DATABASE_BACKUP" || {
        restart_old_after_backup_failure || true
        die "Unable to set database backup permissions; production binary and database were not changed."
    }
    sqlite_integrity_ok "$DATABASE_BACKUP" || {
        restart_old_after_backup_failure || true
        die "Database backup integrity_check was not ok; production binary and database were not changed."
    }

    log "[remote 5/7] Atomically replace and start"
    if ! install_prepared_binary; then
        FAIL_REASON="Unable to atomically install the new binary."
    elif ! sudo systemctl start "$SERVICE"; then
        FAIL_REASON="Unable to start $SERVICE with the new binary."
    fi

    log "[remote 6/7] Critical health checks"
    if [[ -z "$FAIL_REASON" ]] && ! service_is_active; then
        FAIL_REASON="Service is not active after database deployment."
    fi
    if [[ -z "$FAIL_REASON" ]] && ! wait_http_200 "$LOCAL_SWAGGER" 15 2; then
        FAIL_REASON="Local Swagger did not return HTTP 200."
    fi
    if [[ -z "$FAIL_REASON" ]] && ! production_sha_matches; then
        FAIL_REASON="Production binary SHA256 does not match the uploaded binary."
    fi
    if [[ -z "$FAIL_REASON" ]] && ! sqlite_integrity_ok "$DATABASE"; then
        FAIL_REASON="Production database integrity_check was not ok."
    fi
    if [[ -z "$FAIL_REASON" && -n "$EXPECTED_COLUMN" ]] && \
        ! schema_column_exists "$DATABASE" "$EXPECTED_TABLE" "$EXPECTED_COLUMN"; then
        FAIL_REASON="Expected schema column was not found: $EXPECTED_TABLE.$EXPECTED_COLUMN"
    fi

    if [[ -n "$FAIL_REASON" ]]; then
        rollback_database "$DATABASE_BACKUP" || true
        die "$FAIL_REASON"
    fi

    if probe_http_200 "$PUBLIC_SWAGGER"; then
        log "Public Swagger HTTP 200."
    else
        log "WARNING: public Swagger returned HTTP $HTTP_STATUS; database rollback is not triggered because critical local checks passed."
    fi
    log "[remote 7/7] DEPLOY_OK"
}

case "$MODE" in
    CODE) deploy_code ;;
    DATABASE) deploy_database ;;
    *) die "Unsupported mode: $MODE" ;;
esac
'@

    return $bash.Replace("__MODE__", $DeployMode.ToUpperInvariant()).
        Replace("__EXPECTED_SHA256__", $ExpectedSha256.ToLowerInvariant()).
        Replace("__EXPECTED_TABLE__", $ExpectedTable).
        Replace("__EXPECTED_COLUMN__", $ExpectedColumnName)
}

function Test-GeneratedBash {
    param([string]$Path, [string]$Content)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "Generated deploy.sh contains a UTF-8 BOM."
    }
    if ($bytes -contains 0x0D) {
        throw "Generated deploy.sh contains CR characters; LF-only was required."
    }

    $requiredMarkers = @(
        "deploy_code()", "deploy_database()", "rollback_code()", "rollback_database()",
        "sqlite_backup()", "PRAGMA integrity_check", "ROLLBACK_OK",
        "CRITICAL_ROLLBACK_NEEDS_MANUAL_CHECK"
    )
    foreach ($marker in $requiredMarkers) {
        if (-not $Content.Contains($marker)) {
            throw "Generated deploy.sh is missing required branch or marker: $marker"
        }
    }

    $forbiddenPatterns = @(
        '(?i)sshpass',
        '(?i)appsettings\.Local\.json',
        '(?i)AdminApi.{0,3}Key',
        '(?i)private.{0,3}signing.{0,3}key'
    )
    foreach ($pattern in $forbiddenPatterns) {
        if ($Content -match $pattern) {
            throw "Generated deploy.sh contains a forbidden secret-related pattern: $pattern"
        }
    }

    $bashCommand = Get-Command bash -ErrorAction SilentlyContinue
    $bashPath = if ($bashCommand) { $bashCommand.Source } else { "C:\Program Files\Git\bin\bash.exe" }
    if (Test-Path -LiteralPath $bashPath -PathType Leaf) {
        $Content | & $bashPath -n
        if ($LASTEXITCODE -ne 0) {
            throw "Generated deploy.sh failed bash -n validation."
        }
        Write-Host "Generated Bash syntax: OK" -ForegroundColor Green
    }
    else {
        Write-Warning "bash was not found locally; generated Bash syntax validation was skipped."
    }
}

$exitCode = 0
$pauseAtEnd = (-not $NoPause) -and (Get-LaunchedFromExplorer)

try {
    Write-Step 1 "Repository"
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Project was not found relative to the script: $project"
    }
    if ($Mode -eq "Code" -and -not [string]::IsNullOrWhiteSpace($ExpectedColumn)) {
        throw "ExpectedColumn is supported only in Database mode."
    }

    Assert-Command "git"
    $commit = (& git -C $repository rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        throw "Unable to read the current Git commit."
    }
    $dirty = @(& git -C $repository status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect the Git working tree."
    }
    Write-Host "Git commit: $commit"
    if ($dirty.Count -gt 0) {
        Write-Warning "The Git working tree is dirty. The published binary may not match commit $commit."
    }
    else {
        Write-Host "Git working tree: clean"
    }

    if (-not (Test-Path -LiteralPath $publishDirectory)) {
        New-Item -ItemType Directory -Path $publishDirectory | Out-Null
    }

    $expectedTable = ""
    $expectedColumnName = ""
    if (-not [string]::IsNullOrWhiteSpace($ExpectedColumn)) {
        $parts = $ExpectedColumn.Split('.')
        $expectedTable = $parts[0]
        $expectedColumnName = $parts[1]
    }

    if ($ValidateOnly) {
        Write-Step 2 "Build skipped (-ValidateOnly)"
        Write-Step 3 "Generate and validate deploy.sh"
        $validationSha = "0" * 64
        $content = New-RemoteDeployScript $Mode $validationSha $expectedTable $expectedColumnName
        Write-Utf8NoBomLf $generatedBash $content
        Test-GeneratedBash $generatedBash $content
        Write-Step 4 "Upload skipped"
        Write-Step 5 "Remote preflight skipped"
        Write-Step 6 "Deploy skipped"
        Write-Step 7 "Remote verification skipped"
        Write-Step 8 "VALIDATION_OK"
        return
    }

    Write-Step 2 "Build"
    Assert-Command "dotnet"
    if (Test-Path -LiteralPath $binary) {
        Remove-Item -LiteralPath $binary -Force
    }
    Invoke-Native -FilePath "dotnet" -Arguments @(
        "publish", $project,
        "--configuration", "Release",
        "--runtime", "linux-x64",
        "--self-contained", "true",
        "--output", $publishDirectory,
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishTrimmed=false",
        "-p:PublishAot=false"
    )

    $databaseArtifacts = @(Get-ChildItem -LiteralPath $publishDirectory -File | Where-Object {
        $_.Extension -in ".db", ".db-wal", ".db-shm"
    })
    if ($databaseArtifacts.Count -gt 0) {
        throw "Publish output unexpectedly contains SQLite database files."
    }

    Write-Step 3 "Verify"
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "Published binary was not found: $binary"
    }
    $elfMagic = Get-ElfMagic $binary
    if ($elfMagic -ne "7F 45 4C 46") {
        throw "Published file is not ELF. Magic was: $elfMagic"
    }
    $binaryInfo = Get-Item -LiteralPath $binary
    $sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "ELF magic: $elfMagic"
    Write-Host "SHA256: $sha256"
    Write-Host ("Size: {0:N0} bytes ({1:N2} MiB)" -f $binaryInfo.Length, ($binaryInfo.Length / 1MB))

    $content = New-RemoteDeployScript $Mode $sha256 $expectedTable $expectedColumnName
    Write-Utf8NoBomLf $generatedBash $content
    Test-GeneratedBash $generatedBash $content

    if (-not $PSCmdlet.ShouldProcess($SshTarget, "$Mode production deploy of commit $commit")) {
        Write-Step 4 "Upload cancelled"
        Write-Step 8 "CANCELLED"
        return
    }

    Write-Step 4 "Upload"
    Assert-Command "ssh"
    Assert-Command "scp"
    Invoke-Native -FilePath "ssh" -Arguments @(
        $SshTarget, "test -d $remoteStaging -a -w $remoteStaging"
    )
    Invoke-Native -FilePath "scp" -Arguments @($binary, "${SshTarget}:$remoteBinary")
    Invoke-Native -FilePath "scp" -Arguments @($generatedBash, "${SshTarget}:$remoteDeployScript")

    Write-Step 5 "Remote preflight"
    Write-Host "Remote deploy.sh performs ELF, SHA256, sudo, service and current local API checks before stopping production."

    Write-Step 6 "Deploy"
    Invoke-Native -FilePath "ssh" -Arguments @("-t", $SshTarget, "bash $remoteDeployScript")

    Write-Step 7 "Remote verification"
    Write-Host "Remote deploy completed all mode-specific health checks."
    Write-Step 8 "DEPLOY_OK"
}
catch {
    $exitCode = 1
    Write-Host "DEPLOY ERROR" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    if ($pauseAtEnd) {
        [void](Read-Host "Press Enter to close")
    }
}

if ($exitCode -ne 0) {
    exit $exitCode
}
