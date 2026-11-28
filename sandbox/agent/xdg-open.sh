#!/bin/sh
# No browser in the box: print what the CLI wanted to open.
for url do
  printf '  no browser in this box — open this URL on your host:\n\n  %s\n\n' "$url"
done >> "${HOME:-/home/agent}/.vibe/opened-urls" 2>/dev/null || true
exit 0
