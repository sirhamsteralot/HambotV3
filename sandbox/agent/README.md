# Agent box (flavor B)

Least-privilege container for an AI coding agent working on this repo, built
with plain Docker Compose. Generalized procedure and threat model:
`../../sandbox_setup.md` §2.

```
host                                  agent box (containers)
┌──────────────────────────────┐      ┌────────────────────────────────────┐
│ docker daemon  ──── no path ─┼──✕───┤ agent user (uid 1000), cap_drop ALL │
│ ~/.vibe, ~/.ssh ── no path ──┼──✕───┤ read_only rootfs, no-new-privs     │
│ $REPO (working tree) ────────┼──────┤ /work/repo = that tree, read-write │
│                              │      └───────────────┬────────────────────┘
│                              │      ┌───────────────▼────────────────────┐
│                              │      │ sandbox network (internal: true)   │
│ egress proxy (tinyproxy) ◄───┼──────┤ only reachable peer: egress:8888   │
│  default deny + allowlist    │      └────────────────────────────────────┘
│         │                    │
│         ▼ allowed hosts only │
└──────────────────────────────┘
```

## Guarantees

- no host Docker socket, no host home (`~/.vibe`, SSH keys, `~/.gitconfig`),
- exactly one host path: the repo working tree, read-write, at `/work/repo`,
- no network route except the default-deny egress proxy (HTTPS only,
  `ConnectPort 443/563` — no git-over-SSH, no raw TCP),
- `cap_drop: [ALL]` (the `cap_add`s exist only for the root bootstrap that
  drops privileges via `gosu`), read-only rootfs, non-root user,
  `no-new-privileges`, `pids_limit: 2048`, `mem_limit`/`cpus` caps.

## What this does NOT protect

- **The host kernel** — containers share it; a kernel escape is a full
  compromise. The caps/read-only rootfs shrink the surface, they do not make
  it exploit-proof.
- **The working tree is writable from inside the box**, including `.git/`,
  untracked and gitignored files. History is not a safety net. Point
  `REPO_PATH` at a scratch clone (`git clone --shared . /tmp/scratch`) if the
  primary checkout must stay untouched.
- **The mounted repo is readable in full.** Keep secrets out of the tree.
- **The model credential is inside the box.** Pass it via the untracked `.env`,
  never baked into an image; every process in the container can read it.
- **Allowlisted egress is content, not trust.** With a git-hosting line in the
  allowlist the box can read public repos and run whatever their build scripts
  do. The box ships with no credentials of its own.
- **The egress proxy is trusted** — it is the only container with a route out.
- **Host-side compose commands still run as you.**

## Files

| File | Role |
|---|---|
| `Dockerfile` | two targets: `egress` (debian + tinyproxy) and `agent` (.NET 10 SDK + gosu/jq + `mistral-vibe` CLI via uv; the build installs the pinned `AGENT_CLI_VERSION` into `/opt/uv-tools` as the offline fallback) |
| `compose.yaml` | `egress` + `agent` services, internal `sandbox` network, `upstream` |
| `tinyproxy.conf` | default-deny, allowlist filter, HTTPS-only ConnectPorts |
| `allowlist.txt` | one ERE per host, each with a comment saying what deleting it costs |
| `entrypoint.sh` | root bootstrap → `gosu agent`; sets git `safe.directory`; best-effort `uv tool upgrade/install mistral-vibe` into the home volume on every container start |
| `xdg-open.sh` | no-browser stub: prints the URL the CLI wanted to open |
| `.env.example` | copy to `.env` (gitignored) for `MISTRAL_API_KEY` |
| `verify.sh` | boundary verification, run inside the box (see below) |
| `vibe.sh` / `vibe.ps1` | wrappers: start egress, run `vibe` in the box, forward args verbatim |

## Quick start

```sh
cd sandbox/agent
cp .env.example .env                 # optional: MISTRAL_API_KEY (API-key sign-in)
docker compose build
docker compose up -d egress          # the allowlisting proxy
docker compose run --rm agent vibe   # agent session in /work/repo
docker compose run --rm agent        # plain shell in the box
./vibe.sh "first prompt"             # wrapper: proxy + agent in one call
REPO_PATH=/tmp/scratch-clone docker compose run --rm agent vibe   # scratch checkout
```

The CLI is updated to the latest PyPI release on every container start:
`entrypoint.sh` runs `uv tool upgrade/install mistral-vibe` into the
`agent-home` volume through the egress proxy (`UV_TOOL_DIR`/`UV_TOOL_BIN_DIR`
point at `/home/agent/.local`, which is first on `PATH`). If that fails, the
image-pinned `AGENT_CLI_VERSION` copy in `/usr/local/bin` is the fallback. The
in-session self-update is disabled (`VIBE_ENABLE_UPDATE_CHECKS=false` in
`compose.yaml`) so the entrypoint stays the single update path.

Sign-in without a browser: run `vibe --setup` in the box, let the `xdg-open`
stub print the sign-in URL, open it in a host browser; the box keeps polling
the allowlisted console host and finishes on its own. Alternatively set
`MISTRAL_API_KEY` in `.env` and skip browser sign-in.

Reviewing a session: the box wrote straight into the host checkout, so
`git status` / `git diff` / `git add -p` on the host is the review. Nothing
leaves the box by itself — with no credentials inside it, `git push` cannot
authenticate.

Git Bash on Windows: set `MSYS_NO_PATHCONV=1` when passing container paths
(`/work/repo/...`) to `docker compose run`.

## Open in VS Code

The repo root has `.devcontainer/devcontainer.json`, which points at this
compose file (`service: agent`, `workspaceFolder: /work/repo`). With the
Remote - Containers extension installed:

1. `docker compose build` here (once, and after any Dockerfile change),
2. in VS Code: **Dev Containers: Reopen in Container** → picks up
   `.devcontainer/devcontainer.json` and opens `/work/repo` inside the box.

The container stays up detached (`tty` keeps the entrypoint's shell alive),
the VS Code server is injected host-side over the Docker socket into
`/home/agent/.vscode-server` (the `agent-home` volume — writable), and
extension downloads go through the egress proxy (marketplace/CDN hosts are
allowlisted below). The C# extension (`ms-dotnettools.csharp`) is
pre-configured in `devcontainer.json`. Equivalent manual path:
`docker compose up -d` here, then **Remote Explorer → Attach to Running
Container** → the `agent` container.

## Allowlist

| Host | Why |
|---|---|
| `api.mistral.ai`, `api.eu.mistral.ai`, `api.us.mistral.ai` | Mistral model API (default + regional hosts hardcoded in mistral-vibe) |
| `console.mistral.ai`, `chat.mistral.ai` | browser sign-in (PKCE) + Vibe Code subscription base |
| `github.com`, `api.github.com`, `codeload.github.com` | git over HTTPS + REST API + archives (this repo's remote is GitHub) |
| `pypi.org`, `files.pythonhosted.org` | the entrypoint's per-start CLI update (`uv tool upgrade/install mistral-vibe` into the agent-home volume) and `uv tool install` inside the box |
| `api.nuget.org` | `dotnet restore/build/test` inside the box |
| `marketplace.visualstudio.com`, `*.gallery.vsassets.io`, `*.gallerycdn.vsassets.io`, `*.vscode-cdn.net`, `update.code.visualstudio.com` | VS Code extension gallery/CDN, so the dev container can install extensions from inside the box |

Add a host only when the workflow genuinely needs it; keep the comment in
`allowlist.txt` saying what deleting it costs.

## Verification (2026-11-27, Docker Desktop 29.9.2)

`docker compose run --rm agent bash /work/repo/sandbox/agent/verify.sh` —
11 passed, 0 failed:

| Check | Result |
|---|---|
| `id -u` inside the box | PASS (1000) |
| effective capabilities (`CapEff`) | PASS (all zero) |
| `/var/run/docker.sock` inside the box | PASS (absent) |
| write `/work/repo/.probe` → persisted to the mount, then removed | PASS |
| `git -C /work/repo rev-parse --is-inside-work-tree` | PASS (true) |
| sandbox network has no default gateway (`internal: true`) | PASS |
| allowlisted host via proxy (`api.nuget.org`) | PASS (200) |
| VS Code marketplace via proxy (`marketplace.visualstudio.com`) | PASS (200) |
| non-allowlisted host via proxy (`example.com`) | PASS (403 Filtered on the CONNECT) |
| direct egress without the proxy | PASS (blocked) |
| `vibe --version` in the box | PASS (vibe 3.0.2) |

Host-side `docker compose config`: `read_only: true`, `cap_drop: [ALL]`,
`no-new-privileges:true`, `pids_limit: 2048`, `mem_limit: 8g`, `cpus: 8` on
the agent service; exactly one host bind (`<repo>` → `/work/repo`) plus the
`agent-home` named volume; `sandbox` network `internal: true`.

The repo's own suite inside the box (scratch clone in `/tmp`, see the Windows
note below): `dotnet build HambotV2.sln` 0 warnings / 0 errors,
`dotnet test HambotV2.sln` Passed 737 / Failed 0 — same as in the flavor-A
sandbox.

Windows note: Docker Desktop maps host-created files to `root:root` inside
the mount, so inside the locked-down box (uid 1000, `cap_drop ALL`) MSBuild
cannot set timestamps on stale host-built `obj/` files (MSB3374). Run the
suite against a scratch clone — `git clone --shared /work/repo /tmp/scratch`
then `dotnet test` there — or build the tree inside the box first.

VS Code dev-container mechanics: `docker compose up -d` keeps the `agent`
container Up (tty), `docker exec -u agent` works, `/home/agent` (the
vscode-server install path) is writable, and the marketplace answers 200 via
the proxy.

Not verified: the actual VS Code GUI flow ("Reopen in Container" / "Attach to
Running Container" — no VS Code in the verification environment; the
underlying container behavior above is what those commands drive) and a live
signed-in `vibe` session against a real account (the box ships with no
credential; `vibe --version` and the full network boundary were exercised
instead).
