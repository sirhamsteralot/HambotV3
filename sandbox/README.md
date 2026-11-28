# Toolchain sandbox (flavor A)

One image carrying everything this repo needs to run its own verification
(.NET 10 SDK + git + basics), with the repo bind-mounted read-write at
`/work/repo`. The image is a toolchain; the repository is the mount. Nothing
of the repo is baked into the image, and the build context is this directory,
so a rebuild never streams the working tree to the daemon. Generalized
procedure: `../sandbox_setup.md` §1.

```
sandbox/
├── Dockerfile      # mcr.microsoft.com/dotnet/sdk:10.0 + git/git-lfs/jq/... + non-root dev
├── compose.yaml    # build context = this dir; mounts ..:/work/repo + nuget-cache volume
└── README.md
```

## Quick start

```sh
cd sandbox
SANDBOX_UID=$(id -u) SANDBOX_GID=$(id -g) docker compose build
docker compose run --rm sandbox dotnet build HambotV2.sln
docker compose run --rm sandbox dotnet test HambotV2.sln
docker compose run --rm sandbox          # interactive shell in /work/repo
```

On Windows (Git Bash), prefix path arguments with `MSYS_NO_PATHCONV=1` so
`/work/...` is not rewritten to a Windows path, e.g.:

```sh
MSYS_NO_PATHCONV=1 docker compose run --rm sandbox bash /work/repo/scripts/...
```

## What is inside

| Piece | Value |
|---|---|
| Base image | `mcr.microsoft.com/dotnet/sdk:10.0` (same tag the bot's `HambotV2/Dockerfile` builds with) |
| apt packages | ca-certificates, curl, git, git-lfs, jq, less, procps, unzip, vim-tiny |
| Account | non-root `dev`, uid/gid from `SANDBOX_UID`/`SANDBOX_GID` (default 1000), `HOME=/home/dev` |
| Persistent state | named volume `nuget-cache` at `/home/dev/.nuget/packages` (wiped by `down -v`) |
| Mount | `..` → `/work/repo`, read-write |
| Env | `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`, `NUGET_XMLDOC_MODE=skip` |

## Windows note: run as root inside the box

Docker Desktop maps host-created files to `root:root` inside the mount, so a
non-root container cannot set timestamps on stale host-built `obj/` files
(MSBuild error MSB3374). Windows ignores unix ownership, so nothing is lost by
running as root there:

```sh
SANDBOX_USER=0:0 docker compose run --rm sandbox dotnet test HambotV2.sln
```

On Linux/macOS the default `dev` account with matching `SANDBOX_UID`/`SANDBOX_GID`
is the right mode. This sandbox is a build/test convenience, **not a security
boundary** — for that, use `agent/`.

## Verified (2026-11-27, Docker Desktop 29.9.2, image `hambotv2-sandbox:latest` 937MB)

| Check | Result |
|---|---|
| `docker compose build` | OK |
| `dotnet build HambotV2.sln` in the box | succeeded, 0 warnings, 0 errors |
| `dotnet test HambotV2.sln` in the box | Passed: 737, Failed: 0, Skipped: 0 |
| NuGet cache persistence | restores served from the `nuget-cache` volume across `--rm` runs |

Not verified: `dotnet ef` migrations against a live PostgreSQL (needs the
root `docker-compose.yml` db service; tests run without a database by design),
running the bot itself (needs a Discord token).
