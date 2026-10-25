#!/usr/bin/env bash
# HambotV2 scripted restore procedure (FR-83, NFR-19; design §5.2).
#
# The authoritative recovery path for a broken bot or database — it runs from
# outside the stack, where the in-bot !restorebackup command cannot help. The
# in-bot command is the convenient revert for a *healthy* bot.
#
# Usage: scripts/restore-backup.sh <dump-file>
#
# <dump-file> must be readable from this host. Dumps inside the `backups` volume
# can be copied out first:
#   docker compose cp backup:/backups/hambotv2-20260101-030000.dump ./dump.dump
#
# Steps (FR-83):
#   1. Validate the dump's restore catalog before touching anything.
#   2. Stop the bot container.
#   3. Drop and recreate the database (the restore replaces ALL current data).
#   4. pg_restore the dump into the recreated database.
#   5. Start the stack and verify the known startup lines — the same
#      verification the deploy pipeline performs (scripts/deploy.sh, §5.1).

set -euo pipefail

if [ $# -ne 1 ]; then
    echo "Usage: $0 <dump-file>" >&2
    exit 1
fi

dump_file="$1"

cd "$(dirname "$0")/.."

if [ ! -f "$dump_file" ]; then
    echo "==> ERROR: dump file '$dump_file' not found." >&2
    exit 1
fi

fail() {
    echo "==> Restore FAILED: $1" >&2
    echo "==> Last 100 bot log lines:" >&2
    docker compose logs --tail 100 bot >&2 || true
    exit 1
}

echo "==> Validating the dump's restore catalog..."
if ! docker compose exec -T db pg_restore --list < "$dump_file" > /dev/null; then
    fail "the dump file is not a readable pg_dump custom-format archive"
fi

db_user="$(docker compose exec -T db printenv POSTGRES_USER)"
db_name="$(docker compose exec -T db printenv POSTGRES_DB)"

echo "==> Stopping the bot..."
docker compose stop bot

echo "==> Dropping and recreating the database '$db_name' (all current data is replaced)..."
docker compose exec -T db psql -U "$db_user" -d postgres -v ON_ERROR_STOP=1 \
    -v dbname="$db_name" -v dbowner="$db_user" <<SQL
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = :'dbname';
DROP DATABASE :"dbname";
CREATE DATABASE :"dbname" OWNER :"dbowner";
SQL

echo "==> Restoring the dump..."
docker compose exec -T db pg_restore -U "$db_user" --dbname "$db_name" < "$dump_file"

echo "==> Starting the stack..."
restore_started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
docker compose up -d --no-build

echo "==> Waiting for the bot's startup lines..."
deadline=$((SECONDS + 180))
while :; do
    logs="$(docker compose logs --since "$restore_started" bot 2>/dev/null || true)"
    if printf '%s\n' "$logs" | grep -qF 'Database migrations applied successfully' \
        && printf '%s\n' "$logs" | grep -qF 'Discord gateway connection established.' \
        && printf '%s\n' "$logs" | grep -qF 'Bot version:'; then
        break
    fi
    bot_id="$(docker compose ps -q bot)"
    if [ -z "$bot_id" ] || [ "$(docker inspect -f '{{.State.Status}}' "$bot_id")" != "running" ]; then
        fail "bot container is not running"
    fi
    if [ "$SECONDS" -ge "$deadline" ]; then
        fail "bot did not reach its startup lines within 180s"
    fi
    sleep 5
done

echo "==> Restore succeeded: the bot is running on the restored database ($(basename "$dump_file"))."
