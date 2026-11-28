#!/usr/bin/env bash
set -euo pipefail

if [ "$(id -u)" = 0 ]; then
  chown -R agent:agent /home/agent 2>/dev/null || true
  exec gosu agent /usr/local/bin/entrypoint.sh "$@"
fi

git config --global --add safe.directory '*' 2>/dev/null || true

exec "$@"
