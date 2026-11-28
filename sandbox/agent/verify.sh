#!/usr/bin/env bash
# Boundary verification for the agent box — proves the posture instead of
# asserting it. Run from sandbox/agent on the host:
#
#   docker compose run --rm agent bash /work/repo/sandbox/agent/verify.sh
#   docker compose run --rm agent bash /work/repo/sandbox/agent/verify.sh --with-tests
#
# The host-side half (docker compose config shows read_only, cap_drop ALL,
# no-new-privileges, pids_limit, and exactly one host bind) is run on the host;
# see README.md for the recorded result.
set -uo pipefail

pass=0
fail=0
ok()  { printf 'PASS  %s\n' "$1"; pass=$((pass + 1)); }
bad() { printf 'FAIL  %s\n' "$1"; fail=$((fail + 1)); }
check() { # check <label> <actual> <expected>
  if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected [$3], got [$2])"; fi
}

echo "== identity and privileges =="
check "runs as uid 1000" "$(id -u)" "1000"
check "effective capabilities are all zero" "$(grep CapEff /proc/self/status | awk '{print $2}')" "0000000000000000"
if [ ! -e /var/run/docker.sock ]; then ok "no host docker socket mounted"; else bad "no host docker socket mounted"; fi

echo "== the one host path =="
if echo probe > /work/repo/.probe 2>/dev/null && [ -f /work/repo/.probe ]; then
  ok "repo mount is writable (probe file persisted into the tree)"
  rm -f /work/repo/.probe
else
  bad "repo mount is writable"
fi
check "mounted tree is a git work tree" "$(git -C /work/repo rev-parse --is-inside-work-tree 2>/dev/null)" "true"

echo "== network =="
if [ -z "$(ip route show default 2>/dev/null)" ]; then ok "sandbox network has no default gateway"; else bad "sandbox network has no default gateway"; fi
code=$(curl -s -o /dev/null -m 15 -w '%{http_code}' -x http://egress:8888 https://api.nuget.org/v3/index.json 2>/dev/null)
if [ "$code" = "200" ]; then ok "allowlisted host answers via the proxy (api.nuget.org -> 200)"; else bad "allowlisted host answers via the proxy (got [$code])"; fi
code=$(curl -s -o /dev/null -m 15 -w '%{http_code}' -x http://egress:8888 https://marketplace.visualstudio.com/ 2>/dev/null)
if [ -n "$code" ] && [ "$code" != "000" ]; then ok "VS Code marketplace answers via the proxy (HTTP $code)"; else bad "VS Code marketplace answers via the proxy (got [$code])"; fi
# A denied CONNECT gets its 403 on the tunnel itself, so %{http_code} stays 000;
# grep the verbose transcript for the proxy's "403 Filtered" response instead.
out=$(curl -sv -m 15 -x http://egress:8888 https://example.com/ -o /dev/null 2>&1)
if echo "$out" | grep -q '403 Filtered'; then
  ok "non-allowlisted host is refused by the proxy (403 Filtered)"
else
  bad "non-allowlisted host is refused by the proxy (got: $(echo "$out" | grep -oE 'HTTP/1.1 [0-9]+[^\\r]*' | head -1))"
fi
if curl -s -o /dev/null -m 10 --noproxy '*' https://api.nuget.org/v3/index.json 2>/dev/null; then
  bad "direct egress without the proxy is blocked"
else
  ok "direct egress without the proxy is blocked"
fi
# DNS must not bypass the proxy either: on the internal sandbox network the
# container resolves via Docker's embedded resolver (127.0.0.11), which on
# engines without the GHSA-mq39-4gv4-mvpx patch forwards external lookups to
# host resolvers — a DNS-tunneling channel that never touches the proxy.
if getent hosts egress >/dev/null 2>&1; then
  ok "service discovery still resolves 'egress'"
else
  bad "service discovery still resolves 'egress'"
fi
if getent hosts example.com >/dev/null 2>&1; then
  bad "external DNS lookup was answered (embedded resolver forwards external queries — engine lacks the GHSA-mq39-4gv4-mvpx patch?)"
else
  ok "external DNS lookup is not answered (no DNS exfil channel)"
fi

echo "== agent CLI =="
if command -v vibe >/dev/null 2>&1; then
  ok "vibe runs in the box ($(vibe --version 2>/dev/null | head -1))"
else
  bad "vibe runs in the box (not installed — AGENT=none build?)"
fi

if [ "${1:-}" = "--with-tests" ]; then
  echo "== the repo's own verification inside the box =="
  if dotnet build /work/repo/HambotV2.sln --nologo -v q 2>&1 | tail -5; then
    ok "dotnet build HambotV2.sln"
  else
    bad "dotnet build HambotV2.sln"
  fi
  if dotnet test /work/repo/HambotV2.sln --nologo 2>&1 | tail -15; then
    ok "dotnet test HambotV2.sln"
  else
    bad "dotnet test HambotV2.sln"
  fi
fi

echo
echo "result: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
