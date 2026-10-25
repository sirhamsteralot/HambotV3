#!/usr/bin/env bash
# HambotV2 server-side deploy script.
#
# Invoked by the Jenkins pipeline (the "Deploy" stage in the Jenkinsfile) over
# SSH, after the deploy directory has been checked out at the commit to deploy.
# It only ever runs after a successful build and test run, and only for version
# tags (v<major.minor.patch>).
#
# Guarantees (design §5.1):
#   - The image is built before any container is replaced, so a failed build
#     leaves the currently running stack untouched.
#   - After starting the new container, the script waits for the database to be
#     healthy and for the bot's known startup lines ("Database migrations
#     applied successfully" and "Discord gateway connection established.").
#     If they do not appear in time, the previously running image is restored
#     and the script exits non-zero, so Jenkins fails the job instead of
#     shipping a broken bot.
#   - On success, images left dangling by the rebuild are pruned so the disk
#     does not fill up.
#
# Usage: deploy.sh <commit-sha> <jenkins-build-number> <jenkins-build-url>
#
# Secrets never pass through this script or the pipeline: docker compose reads
# the gitignored .env next to docker-compose.yml on this server.

set -euo pipefail

commit="$1"
build_number="$2"
build_url="$3"

cd "$(dirname "$0")/.."

# Compose v2's default image name is <project>-<service>; the project name is
# derived from the directory name (/opt/hambotv2 -> hambotv2-bot).
image_ref="$(basename "$(pwd)")-bot"

# Snapshot the currently running bot image (if any) as the rollback point
# before the rebuild retags hambotv2-bot and leaves the old image dangling.
rollback_image=""
rollback_container_id="$(docker compose ps -q bot 2>/dev/null || true)"
if [ -n "$rollback_container_id" ] && docker inspect "$rollback_container_id" >/dev/null 2>&1; then
    rollback_image="$(docker inspect -f '{{.Image}}' "$rollback_container_id")"
fi

deploy_started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

fail() {
    echo "==> Deploy verification FAILED: $1" >&2
    echo "==> Last 100 bot log lines:" >&2
    docker compose logs --tail 100 bot >&2 || true
    if [ -n "$rollback_image" ]; then
        echo "==> Rolling back to the previously running image" >&2
        docker tag "$rollback_image" "$image_ref"
        docker compose up -d --no-build >&2 || true
    else
        echo "==> No previous image to roll back to (first deploy?)" >&2
    fi
    exit 1
}

echo "==> Deploying commit ${commit} (Jenkins build #${build_number})"

# 1. Build the new image before touching any container: a failed build must
#    not disturb the running stack (deploy failure != outage).
docker compose build bot

# 2. Replace only changed containers. --no-build: exactly the image built above
#    is run. Compose recreates only the bot when its image changed; the db
#    keeps its data volume, and compose itself waits for its healthcheck
#    before starting the bot.
docker compose up -d --no-build

# 3. Verify the deployment actually came up instead of failing silently.

# 3a. Database healthy (normally instant: it was already running).
db_id="$(docker compose ps -q db)"
deadline=$((SECONDS + 60))
while [ "$(docker inspect -f '{{.State.Health.Status}}' "$db_id")" != "healthy" ]; do
    if [ "$SECONDS" -ge "$deadline" ]; then
        fail "database did not become healthy within 60s"
    fi
    sleep 3
done

# 3b. Bot startup lines within 3 minutes.
echo "==> Waiting for bot startup lines..."
deadline=$((SECONDS + 180))
while :; do
    logs="$(docker compose logs --since "$deploy_started" bot 2>/dev/null || true)"
    if printf '%s\n' "$logs" | grep -qF 'Database migrations applied successfully' \
        && printf '%s\n' "$logs" | grep -qF 'Discord gateway connection established.'; then
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

# 4. Deployment healthy: prune the images left dangling by this rebuild so the
#    disk does not fill up (dangling only; tagged images are kept).
docker image prune -f

# 5. Record what is now running (untracked file next to the checkout) so the
#    deployment is traceable alongside the Jenkins build.
printf 'commit=%s\nbuild=%s\nbuild_url=%s\ndeployed_at=%s\n' \
    "$commit" "$build_number" "$build_url" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > .deploy-info

echo "==> Deploy succeeded: commit ${commit} (Jenkins build #${build_number}) is live."
