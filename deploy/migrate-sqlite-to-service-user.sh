#!/usr/bin/env bash
# Copies the legacy SQLite database into the service-owned state directory.
# It never deletes or renames the legacy database. Run on the Linux host as root.
set -Eeuo pipefail
umask 0077

SOURCE_DATABASE="/opt/honestserver/HonestLicenseFull.db"
TARGET_DIRECTORY="/var/lib/honestserver"
TARGET_DATABASE="$TARGET_DIRECTORY/HonestLicenseFull.db"
BACKUP_DIRECTORY="$TARGET_DIRECTORY/backups"
SERVICE="honestserver"
SERVICE_USER="honestflow-svc"
SERVICE_GROUP="honestflow-svc"
APPLY=false
LEAVE_SERVICE_STOPPED=false
SERVICE_WAS_ACTIVE=false
SERVICE_STOPPED=false
TEMPORARY_DATABASE=""

usage() {
    cat <<'EOF'
Usage: sudo ./migrate-sqlite-to-service-user.sh --apply [options]

Copies /opt/honestserver/HonestLicenseFull.db to /var/lib/honestserver using
SQLite's backup API after a WAL checkpoint. The legacy database is preserved.

Options:
  --apply                    Perform the migration. Without this flag, only show the plan.
  --leave-service-stopped    Do not restart the currently active legacy service after success.
  --source PATH              Legacy database path.
  --target-directory PATH    Service-owned state directory.
  --service NAME             systemd service to stop during the consistent backup.
  --service-user NAME        Owner of the target database (default: honestflow-svc).
  --service-group NAME       Group of the target database (default: honestflow-svc).
  -h, --help                 Show this help.
EOF
}

log() {
    printf '%s\n' "$*"
}

die() {
    printf 'MIGRATION ERROR\n%s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "Required command is missing: $1"
}

restart_legacy_service() {
    if [[ "$SERVICE_WAS_ACTIVE" == true && "$SERVICE_STOPPED" == true ]]; then
        log "Restarting the unchanged legacy service."
        systemctl start "$SERVICE" || {
            log "CRITICAL: unable to restart $SERVICE; the legacy database was not changed."
            return 1
        }
        SERVICE_STOPPED=false
    fi
}

on_error() {
    local exit_code=$?
    trap - ERR EXIT
    if [[ -n "$TEMPORARY_DATABASE" && -e "$TEMPORARY_DATABASE" ]]; then
        rm -f -- "$TEMPORARY_DATABASE"
    fi
    restart_legacy_service || true
    exit "$exit_code"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --apply) APPLY=true ;;
        --leave-service-stopped) LEAVE_SERVICE_STOPPED=true ;;
        --source)
            SOURCE_DATABASE="${2:?--source requires a path}"
            shift
            ;;
        --target-directory)
            TARGET_DIRECTORY="${2:?--target-directory requires a path}"
            shift
            ;;
        --service)
            SERVICE="${2:?--service requires a name}"
            shift
            ;;
        --service-user)
            SERVICE_USER="${2:?--service-user requires a name}"
            shift
            ;;
        --service-group)
            SERVICE_GROUP="${2:?--service-group requires a name}"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *) die "Unknown argument: $1" ;;
    esac
    shift
done

TARGET_DATABASE="$TARGET_DIRECTORY/HonestLicenseFull.db"
BACKUP_DIRECTORY="$TARGET_DIRECTORY/backups"

[[ -f "$SOURCE_DATABASE" ]] || die "Legacy database was not found: $SOURCE_DATABASE"
[[ -d "$TARGET_DIRECTORY" ]] || die "Target directory was not found: $TARGET_DIRECTORY"
[[ ! -e "$TARGET_DATABASE" && ! -e "${TARGET_DATABASE}-wal" && ! -e "${TARGET_DATABASE}-shm" ]] || \
    die "Target database or its WAL sidecars already exist; refusing to overwrite them."

if [[ "$APPLY" != true ]]; then
    log "Dry run: $SOURCE_DATABASE would be copied to $TARGET_DATABASE."
    log "The script would stop $SERVICE, checkpoint WAL, create an integrity-checked backup, and restart the unchanged service."
    log "Re-run with --apply to perform the migration."
    exit 0
fi

[[ "$(id -u)" -eq 0 ]] || die "Run this script through sudo as root."
for command_name in python3 systemctl install date id getent sudo; do
    require_command "$command_name"
done
python3 -c 'import sqlite3' || die "Python sqlite3 module is unavailable."
id "$SERVICE_USER" >/dev/null 2>&1 || die "Service user was not found: $SERVICE_USER"
getent group "$SERVICE_GROUP" >/dev/null 2>&1 || die "Service group was not found: $SERVICE_GROUP"
sudo -u "$SERVICE_USER" test -r "$TARGET_DIRECTORY" -a -w "$TARGET_DIRECTORY" -a -x "$TARGET_DIRECTORY" || \
    die "Service user $SERVICE_USER does not have read/write/search access to $TARGET_DIRECTORY."

trap on_error ERR EXIT

if systemctl is-active --quiet "$SERVICE"; then
    SERVICE_WAS_ACTIVE=true
    log "Stopping $SERVICE for a consistent SQLite checkpoint."
    systemctl stop "$SERVICE"
    SERVICE_STOPPED=true
    systemctl is-active --quiet "$SERVICE" && die "$SERVICE did not stop."
fi

TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
BACKUP_DATABASE="$BACKUP_DIRECTORY/HonestLicenseFull.pre-service-user-${TIMESTAMP}.db"
TEMPORARY_DATABASE="$TARGET_DIRECTORY/.HonestLicenseFull.db.migrating-${TIMESTAMP}"
install -d -m 700 -o root -g root "$BACKUP_DIRECTORY"

log "Checking source integrity and checkpointing WAL."
python3 - "$SOURCE_DATABASE" "$BACKUP_DATABASE" "$TEMPORARY_DATABASE" <<'PY'
import os
import shutil
import sqlite3
import sys

source_path, backup_path, temporary_path = sys.argv[1:4]

def integrity_check(path: str) -> None:
    connection = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    try:
        result = connection.execute("PRAGMA integrity_check").fetchone()[0]
    finally:
        connection.close()
    if result != "ok":
        raise RuntimeError(f"integrity_check for {path} returned: {result}")

source = sqlite3.connect(f"file:{source_path}?mode=rw", uri=True)
try:
    integrity_check(source_path)
    checkpoint = source.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchone()
    if checkpoint is None or checkpoint[0] != 0:
        raise RuntimeError(f"wal_checkpoint(TRUNCATE) did not complete: {checkpoint}")
    print(f"WAL checkpoint result: busy={checkpoint[0]}, log={checkpoint[1]}, checkpointed={checkpoint[2]}")

    backup = sqlite3.connect(backup_path)
    try:
        source.backup(backup)
    finally:
        backup.close()
finally:
    source.close()

integrity_check(backup_path)
shutil.copyfile(backup_path, temporary_path)
with open(temporary_path, "rb") as target:
    os.fsync(target.fileno())
integrity_check(temporary_path)
PY

install -m 600 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$TEMPORARY_DATABASE" "$TEMPORARY_DATABASE.ready"
mv -f -- "$TEMPORARY_DATABASE.ready" "$TARGET_DATABASE"
rm -f -- "$TEMPORARY_DATABASE"
TEMPORARY_DATABASE=""

log "Migration copy and integrity checks: OK"
log "Legacy database remains unchanged at: $SOURCE_DATABASE"
log "Verified rollback source: $BACKUP_DATABASE"

if [[ "$LEAVE_SERVICE_STOPPED" == true ]]; then
    log "Migration complete. $SERVICE remains stopped for the planned unit/environment cutover."
    SERVICE_STOPPED=false
else
    restart_legacy_service
    log "Migration complete. The existing service was restarted with its unchanged legacy configuration."
fi

trap - ERR EXIT
