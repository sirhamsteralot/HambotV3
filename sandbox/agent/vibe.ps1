# Thin wrapper: start the egress proxy, then run the Vibe CLI in the agent box.
# All arguments are forwarded verbatim; the exit code is the container's.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $here
try {
  docker compose up -d egress | Out-Null
  docker compose run --rm agent vibe @args
} finally {
  Pop-Location
}
exit $LASTEXITCODE
