#!/usr/bin/env bash
# HambotV2 backup sidecar loop (FR-81–FR-85, FR-100–FR-101; design §5.2).
#
# Runs as a dedicated compose service next to the bot, so the database is backed
# up on schedule even while the bot container is stopped, restarting, or failing
# (FR-84). Each pass:
#
#   1. pg_dump -Fc into the shared /backups volume, timestamped.
#   2. Validate the dump (FR-100): pg_restore --list must succeed and the file
#      must reach the minimum size floor. An invalid dump is logged, deleted, and
#      counts for nothing — no retention, no offsite push, and a watchdog alert
#      (the bot reads the status file this loop writes).
#   3. Prune local dumps beyond BACKUP_RETENTION, keeping the most recent ones —
#      only after the new dump exists and is validated, so a failed dump never
#      deletes good backups.
#   4. Push the new dump offsite via rclone when BACKUP_RCLONE_REMOTE is
#      configured (FR-82). A failed push is logged and otherwise harmless.
#   5. On BACKUP_TEST_INTERVAL (default monthly), prove restorability (FR-101):
#      restore the newest dump into a scratch database, verify the expected
#      tables, drop the scratch database, record the outcome.
#   6. Write backups/.backup-status — the single status file the bot's !health
#      command and watchdog consume (§3.13).
#
# All configuration comes from environment (compose passes it from the gitignored
# .env); credentials never live in this file or the image (FR-85).

set -euo pipefail

: "${PGHOST:=db}"
: "${PGPORT:=5432}"
: "${PGUSER:=hambot}"
: "${PGDATABASE:=hambotdb}"
: "${PGPASSWORD:?PGPASSWORD must be set (compose passes it from .env)}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
BACKUP_INTERVAL="${BACKUP_INTERVAL:-86400}"                 # seconds between backups (default 24h)
BACKUP_RETENTION="${BACKUP_RETENTION:-7}"                  # most recent dumps to keep locally
BACKUP_TEST_INTERVAL="${BACKUP_TEST_INTERVAL:-2592000}"    # seconds between restore tests (default 30 days)
BACKUP_MIN_SIZE="${BACKUP_MIN_SIZE:-1024}"                 # bytes; a valid dump must reach this size
BACKUP_RCLONE_REMOTE="${BACKUP_RCLONE_REMOTE:-}"           # e.g. "backup:my-bucket/hambotv2"; empty = no offsite push
SCRATCH_DB="${BACKUP_SCRATCH_DB:-hambotv2_verify}"

STATUS_FILE="$BACKUP_DIR/.backup-status"
LAST_TEST_FILE="$BACKUP_DIR/.last-restore-test"            # persisted marker for the test schedule
DUMP_GLOB='hambotv2-*.dump'

# Expected tables of the bot's schema (§3.4) + EF's migration bookkeeping table.
EXPECTED_TABLES=(Users Commands GuildSettings Quotes CustomResponses Reminders BotSettings __EFMigrationsHistory)

# --- Status file (§3.13): one file, two consumers (!health + watchdog) ---

status_last_success_name=""
status_last_success_ts=""
status_last_success_size=0
status_last_error_ts=""
status_last_error_msg=""
status_last_test_ts=""
status_last_test_ok=""
status_last_test_detail=""

log() { echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*"; }
now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }

write_status() {
    jq -n \
        --arg lsn "$status_last_success_name" --arg lst "$status_last_success_ts" --argjson lss "$status_last_success_size" \
        --arg let "$status_last_error_ts" --arg lem "$status_last_error_msg" \
        --arg ltt "$status_last_test_ts" --arg lto "$status_last_test_ok" --arg ltd "$status_last_test_detail" \
        --argjson bis "$BACKUP_INTERVAL" --argjson tis "$BACKUP_TEST_INTERVAL" \
        --arg upd "$(now_iso)" \
        '{
            lastSuccessfulBackup: (if $lsn == "" then null else {name: $lsn, timestamp: $lst, sizeBytes: $lss} end),
            lastValidationError: (if $let == "" then null else {timestamp: $let, message: $lem} end),
            lastRestoreTest: (if $ltt == "" then null else {timestamp: $ltt, succeeded: ($lto == "true"), detail: $ltd} end),
            backupIntervalSeconds: $bis,
            restoreTestIntervalSeconds: $tis,
            updatedAt: $upd
        }' > "$STATUS_FILE.tmp" && mv "$STATUS_FILE.tmp" "$STATUS_FILE"
}

newest_dump() {
    ls -1t "$BACKUP_DIR"/$DUMP_GLOB 2>/dev/null | head -1 || true
}

# FR-100: a dump counts only when its restore catalog lists and it reaches the size floor.
validate_dump() {
    local file="$1" size
    size=$(stat -c%s "$file")
    if [ "$size" -lt "$BACKUP_MIN_SIZE" ]; then
        echo "dump is only $size bytes (below the $BACKUP_MIN_SIZE floor)"
        return 1
    fi
    if ! pg_restore --list "$file" >/dev/null 2>&1; then
        echo "pg_restore --list could not read the dump's catalog"
        return 1
    fi
    return 0
}

# FR-81: prune only after a good dump, keeping the most recent BACKUP_RETENTION dumps.
prune_old_dumps() {
    ls -1t "$BACKUP_DIR"/$DUMP_GLOB 2>/dev/null | tail -n +$((BACKUP_RETENTION + 1)) | while read -r file; do
        log "Pruning old backup: $file"
        rm -f "$file"
    done || true
}

# FR-82: optional offsite push; a failure is logged and otherwise harmless.
push_offsite() {
    local file="$1"
    if [ -z "$BACKUP_RCLONE_REMOTE" ]; then
        log "No offsite remote configured (BACKUP_RCLONE_REMOTE empty) — skipping the push."
        return 0
    fi

    if rclone copy "$file" "$BACKUP_RCLONE_REMOTE"; then
        log "Offsite push succeeded: $(basename "$file") -> $BACKUP_RCLONE_REMOTE"
    else
        log "WARNING: offsite push failed for $(basename "$file") — the local dump, its retention, and the bot are unaffected (FR-82)."
    fi
}

backup_once() {
    local name="hambotv2-$(date -u +%Y%m%d-%H%M%S).dump"
    local path="$BACKUP_DIR/$name"
    log "Starting backup: $name"

    local dump_error
    if ! dump_error=$(pg_dump --format=custom --file="$path" 2>&1); then
        log "ERROR: pg_dump failed: $dump_error"
        rm -f "$path"
        status_last_error_ts="$(now_iso)"
        status_last_error_msg="pg_dump failed: $dump_error"
        write_status
        return 1
    fi

    local size
    size=$(stat -c%s "$path")

    local validation_error
    if ! validation_error=$(validate_dump "$path"); then
        log "ERROR: backup validation failed for $name: $validation_error"
        rm -f "$path"
        status_last_error_ts="$(now_iso)"
        status_last_error_msg="validation failed for $name: $validation_error"
        write_status
        return 1
    fi
    log "Backup validated: $name ($size bytes)"

    status_last_success_name="$name"
    status_last_success_ts="$(now_iso)"
    status_last_success_size="$size"
    write_status

    prune_old_dumps
    push_offsite "$path"
    return 0
}

# FR-101: prove restorability against a scratch database; the live database is never touched.
run_restore_test() {
    local dump
    dump=$(newest_dump)
    local ts
    ts="$(now_iso)"

    if [ -z "$dump" ]; then
        status_last_test_ts="$ts"
        status_last_test_ok="false"
        status_last_test_detail="no dump available to test"
        write_status
        log "ERROR: restore test failed — no dump available."
        return 1
    fi

    log "Running automated restore test against $(basename "$dump")"
    local ok=false detail=""

    dropdb --if-exists "$SCRATCH_DB" 2>/dev/null || true
    if createdb "$SCRATCH_DB" && pg_restore --dbname="$SCRATCH_DB" --no-owner "$dump" 2>/dev/null; then
        local missing=""
        local table
        for table in "${EXPECTED_TABLES[@]}"; do
            if ! psql -qtA -d "$SCRATCH_DB" -c "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='$table'" 2>/dev/null | grep -q 1; then
                missing="$missing $table"
            fi
        done
        if [ -z "$missing" ]; then
            ok=true
            detail="restore succeeded, all expected tables present"
        else
            detail="restore succeeded but tables missing:$missing"
        fi
    else
        detail="pg_restore into the scratch database failed"
    fi
    dropdb --if-exists "$SCRATCH_DB" 2>/dev/null || true

    status_last_test_ts="$ts"
    status_last_test_ok="$ok"
    status_last_test_detail="$detail"
    write_status

    if [ "$ok" = true ]; then
        log "Restore test passed: $detail"
    else
        log "ERROR: restore test failed: $detail"
    fi

    date -u +%s > "$LAST_TEST_FILE" || true
    [ "$ok" = true ]
}

# The restore test is due on the first pass (a fresh deployment proves restorability
# immediately) and then every BACKUP_TEST_INTERVAL seconds.
restore_test_due() {
    if [ ! -f "$LAST_TEST_FILE" ]; then
        return 0
    fi
    local last now
    last=$(cat "$LAST_TEST_FILE")
    now=$(date -u +%s)
    [ $((now - last)) -ge "$BACKUP_TEST_INTERVAL" ]
}

log "Backup sidecar starting: interval=${BACKUP_INTERVAL}s retention=${BACKUP_RETENTION} test-interval=${BACKUP_TEST_INTERVAL}s dir=$BACKUP_DIR"
log "Offsite remote: ${BACKUP_RCLONE_REMOTE:-<none configured>}"
mkdir -p "$BACKUP_DIR"
# The bot container runs as a non-root user but must create dumps here too (the
# !backup/!restorebackup commands, §5.2); the volume root is otherwise root-owned.
# 1777 (like /tmp): anyone may create files, the sticky bit protects them from
# deletion by others. This sidecar runs as root, so it can fix the mode on every
# start — including volumes created root:root by a previous run.
chmod 1777 "$BACKUP_DIR"

# Seed the in-memory status from an existing status file: the variables above reset
# with every container restart, but the last observed backup/restore-test state must
# not — the watchdog reads the file and would otherwise see a false "no test has run"
# gap (alerting the operator) until the next real observation lands.
if [ -f "$STATUS_FILE" ]; then
    status_last_success_name=$(jq -r '.lastSuccessfulBackup.name // ""' "$STATUS_FILE")
    status_last_success_ts=$(jq -r '.lastSuccessfulBackup.timestamp // ""' "$STATUS_FILE")
    status_last_success_size=$(jq -r '.lastSuccessfulBackup.sizeBytes // 0' "$STATUS_FILE")
    status_last_error_ts=$(jq -r '.lastValidationError.timestamp // ""' "$STATUS_FILE")
    status_last_error_msg=$(jq -r '.lastValidationError.message // ""' "$STATUS_FILE")
    status_last_test_ts=$(jq -r '.lastRestoreTest.timestamp // ""' "$STATUS_FILE")
    status_last_test_ok=$(jq -r 'if .lastRestoreTest == null then "" else (.lastRestoreTest.succeeded | tostring) end' "$STATUS_FILE")
    status_last_test_detail=$(jq -r '.lastRestoreTest.detail // ""' "$STATUS_FILE")
    log "Seeded backup status from $STATUS_FILE (last backup: ${status_last_success_ts:-none}, last restore test: ${status_last_test_ts:-none})."
fi

# Compose gates this service on the db healthcheck; this wait is cheap robustness
# for plain-docker runs and database restarts.
until pg_isready -q; do
    log "Waiting for the database..."
    sleep 10
done

# Endless loop: backup pass (then restore test when due), then sleep until the next.
while true; do
    if backup_once; then
        log "Backup pass completed."
    else
        log "Backup pass FAILED — the next attempt is at the next interval."
    fi

    if restore_test_due; then
        run_restore_test || true
    fi

    log "Sleeping ${BACKUP_INTERVAL}s until the next backup."
    sleep "$BACKUP_INTERVAL"
done
