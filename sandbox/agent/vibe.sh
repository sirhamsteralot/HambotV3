#!/usr/bin/env bash
# Thin wrapper: start the egress proxy, then run the Vibe CLI in the agent box.
# All arguments are forwarded verbatim; the exit code is the container's.
set -euo pipefail
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cd "$here"

docker compose up -d egress >/dev/null
exec docker compose run --rm agent vibe "$@"
