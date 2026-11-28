#!/usr/bin/env bash
set -euo pipefail

if [ "$(id -u)" = 0 ]; then
  chown -R agent:agent /home/agent 2>/dev/null || true
  exec gosu agent /usr/local/bin/entrypoint.sh "$@"
fi

git config --global --add safe.directory '*' 2>/dev/null || true

# Best-effort: pull the latest CLI into the writable home volume on every
# container start. If it fails (e.g. egress down) the image-pinned
# /usr/local/bin/vibe remains on PATH as the fallback.
uv tool upgrade mistral-vibe 2>/dev/null || uv tool install mistral-vibe 2>/dev/null || true
[ -x "${UV_TOOL_BIN_DIR:-$HOME/.local/bin}/vibe" ] || \
  echo "agent-box: mistral-vibe update failed; using the image-pinned vibe" >&2

exec "$@"
