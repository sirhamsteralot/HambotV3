# Jenkins CI/CD Setup

How to wire this repository up to [Jenkins](https://www.jenkins.io/) so that every push is built and tested, and — on a version tag (`v<major.minor.patch>`) — automatically deployed to the production server.

The pipeline lives in the repository root [`Jenkinsfile`](../Jenkinsfile) and is meant to be used as **"Pipeline script from SCM"**, so the pipeline definition stays versioned alongside the code it builds.

## What the pipeline does

| Stage | Runs on | What happens |
|---|---|---|
| **Build** | every build | `dotnet build HambotV2.sln --configuration Release /warnaserror:nullable` — the build is warning-free; this fails the job if a commit introduces a nullable warning. |
| **Test** | every build | `dotnet test HambotV2.sln --no-build` with results written as TRX files to `TestResults/`. Tests need no database, Discord token, or Docker. |
| **Deploy** | version tags only | Jenkins SSHes into the production host, checks out the exact commit CI just built and tested in the deploy clone, and runs [`scripts/deploy.sh`](../scripts/deploy.sh). Ordinary branch pushes (including `master`), feature branches, and PR builds stop after Test. |

Safety properties:

- A stage only runs if the previous ones succeeded — **a failed build or test run never deploys anything**.
- The deploy script builds the new Docker image *before* replacing any container, so a failed image build leaves the running stack untouched.
- After starting the new container, the script waits for the database healthcheck and the bot's known startup lines (`Database migrations applied successfully`, `Discord gateway connection established.`, and `Bot version: <version>` matching the deployed tag + commit); if they don't appear in time, it rolls back to the previously running image and the Jenkins job fails.
- Secrets never pass through Jenkins: the production `.env` (gitignored) lives on the server next to `docker-compose.yml` and is read by Compose there.

Besides this build/test/deploy pipeline, a second **scheduled** pipeline — [`Jenkinsfile.security`](../Jenkinsfile.security) — runs a weekly dependency security scan (SBOM + CVE report, never deploys). See [§10](#10-the-weekly-dependency-security-scan-job).

## Setup in the order you'll do it

The steps below are chronological — each one depends on the previous:

1. **Prepare the production server** — deploy user, clone of the repo, `.env` ([§1](#1-prepare-the-production-server)).
2. **Create and authorize the deploy SSH key** — on that same server, then paste the private key into Jenkins ([§2](#2-create-and-authorize-the-deploy-ssh-key)).
3. **Install the .NET 10 SDK** on the Jenkins agent ([§3](#3-install-the-net-10-sdk-on-the-agent)).
4. **Install the Jenkins plugins** ([§4](#4-install-the-required-jenkins-plugins)).
5. **Add the Jenkins credentials** — the deploy SSH key, plus git credentials for the repository ([§5](#5-add-the-jenkins-credentials)).
6. **Point the pipeline at your server** — the `environment` block in the Jenkinsfile ([§6](#6-point-the-pipeline-at-your-server)).
7. **Create the Jenkins job** ([§7](#7-create-the-jenkins-job)).
8. **Run it and verify** ([§8](#8-run-it-and-verify)).
9. **Stopping the stack** — when you need the bot down ([§9](#9-stopping-the-stack)).

## Prerequisites

- A Jenkins controller/agent running **Linux** (the pipeline uses `sh`).
- A production host with **Docker + the Compose plugin** and **git**, reachable from the Jenkins agent over SSH.
- The **.NET 10 SDK** (`10.0.x`) for the Jenkins agent.
- Git credentials for this repository in Jenkins (or a public repo).
- For the weekly scan job (§10) only: **Docker** on the Jenkins agent itself, **jq**, and outbound network access to NuGet, GitHub releases, and `ghcr.io` (the Trivy vulnerability DB).

## 1. Prepare the production server

On the production host, once — this mirrors the "One-time server preparation" block in [`software-design.md`](software-design.md) §5.1:

```bash
# deploy user with Docker access (docker group membership is effectively root-equivalent
# on the host — acceptable for this single-server deployment; harden with rootless
# Docker if that ever changes)
sudo useradd -m hambot-deploy && sudo usermod -aG docker hambot-deploy
sudo mkdir -p /opt/hambotv2 && sudo chown hambot-deploy /opt/hambotv2
sudo -u hambot-deploy git clone https://github.com/sirhamsteralot/HambotV3.git /opt/hambotv2   # deploy key if the repo is private
cd /opt/hambotv2 && sudo -u hambot-deploy cp .env.example .env  # fill in; the pipeline never touches this file
```

Notes:

- **Docker group membership is effectively root-equivalent on the host.** That is accepted for this single-server deployment; harden with rootless Docker if that ever changes.
- If the repository is **private**, give the deploy user's clone a deploy key (read access is enough — the pipeline only fetches).
- Fill in the server's `.env` (`DISCORD_BOT_TOKEN`, `POSTGRES_PASSWORD`, `BOT_OWNER_DISCORD_ID`) there and then; the pipeline never reads, transfers, or modifies this file.

> **Pitfall:** `useradd -m` creates the home directory with mode `750` owned by `hambot-deploy`. That is correct (and required — sshd's `StrictModes` rejects group-writable homes), but it also means **your admin account cannot read the deploy user's files**. Any later inspection of `~hambot-deploy/.ssh/...` needs `sudo`; a plain `stat`/`cat` fails with `Permission denied`, which looks like a problem but isn't.

## 2. Create and authorize the deploy SSH key

Still on the production host: generate the key pair for the deploy user (no passphrase — the pipeline must use it non-interactively), install the public key, and verify each step left what it should have.

```bash
# 1. Generate the key pair as the deploy user. mkdir comes FIRST:
#    ssh-keygen does not create ~/.ssh itself and fails if it doesn't exist.
sudo -u hambot-deploy mkdir -p ~hambot-deploy/.ssh && sudo chmod 700 ~hambot-deploy/.ssh
sudo -u hambot-deploy ssh-keygen -t ed25519 -f ~hambot-deploy/.ssh/id_ed25519 -N "" -C "jenkins-deploy"

# 2. Authorize it for the deploy user (id_ed25519 -> authorized_keys)
sudo -u hambot-deploy sh -c 'cat ~/.ssh/id_ed25519.pub >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys'

# 3. Verify BOTH halves exist and match — do not skip this:
sudo ls -la /home/hambot-deploy/.ssh/                       # id_ed25519, id_ed25519.pub, authorized_keys
sudo ssh-keygen -lf /home/hambot-deploy/.ssh/authorized_keys  # the fingerprint sshd will accept

# 4. Print the private key and copy it for the Jenkins credential in step 5
sudo -u hambot-deploy cat ~hambot-deploy/.ssh/id_ed25519
```

> **Pitfall — the single most common deploy failure:** step 2 is a separate command, and skipping or half-running it produces exactly the Jenkins error `Permission denied (publickey,password)` in the Deploy stage. The keypair existing is *not* enough — sshd only sees `authorized_keys`. If `sudo ls -la` shows `id_ed25519` but no `authorized_keys` (or `ssh-keygen -lf` says `No such file or directory`), re-run step 2 and re-verify.

> **Pitfall — permissions are enforced silently:** if `~/.ssh` is not `700`, `authorized_keys` is not `600`, or either is owned by another user, sshd's `StrictModes` ignores the key **without any visible error** — the connection just falls through to password auth. The commands above set the right modes; only change them knowingly.

Keep the fingerprint from step 3 — in [§5](#5-add-the-jenkins-credentials) you'll confirm the Jenkins credential holds the matching private key.

## 3. Install the .NET 10 SDK on the agent

The Jenkinsfile sets:

```groovy
environment {
    DOTNET_ROOT = '/opt/dotnet10'
    PATH = "/opt/dotnet10:${env.PATH}"
    ...
}
```

Install the SDK and put it (or a symlink) at `/opt/dotnet10`, **or** edit those two lines to point at wherever `dotnet` lives on your agent (e.g. `/usr/lib/dotnet`, `/usr/share/dotnet`). Verify with:

```bash
/opt/dotnet10/dotnet --version   # should print 10.0.x
```

## 4. Install the required Jenkins plugins

Manage Jenkins → Plugins → Available:

- **Pipeline** (usually preinstalled) — declarative pipeline support.
- **Git** (usually preinstalled) — checkout and "Pipeline script from SCM".
- **SSH Agent** — provides the `sshagent` step used by the Deploy stage. Without it the Deploy stage fails with `sshagent` step not found.
- Optional: **Publish Test results** / an xUnit plugin to render the TRX files nicely (the pipeline archives `TestResults/**/*.trx` either way, so results are inspectable even without one).

## 5. Add the Jenkins credentials

Two credentials in Jenkins: the deploy SSH key from [§2](#2-create-and-authorize-the-deploy-ssh-key), and (for a private repo) the git token.

**Deploy SSH key** — Manage Jenkins → Credentials → add **SSH Username with private key**:

1. Paste the *private* key printed by step 4 of §2 (`id_ed25519`, starting with `-----BEGIN OPENSSH PRIVATE KEY-----`).
2. Set the username to `hambot-deploy` (i.e. `DEPLOY_USER`).
3. Give the credential the ID used in `DEPLOY_SSH_CREDENTIAL` (`hambot-deploy-ssh-key` by default — pick your own ID and update the Jenkinsfile if you prefer).

**Git** — for a private repository, add the PAT/deploy token the job uses for checkout.

Verify the two sides match before the first deploy — the authorized fingerprint from §2 step 3 must be the public half of the key now stored in Jenkins. If in doubt, check what the agent actually offers:

```bash
# from the Jenkins container: the key the agent offers (rejects the password
# prompt, but prints the fingerprint of the offered key before failing)
ssh -v hambot-deploy@172.17.0.1 2>&1 | grep 'offering public key'
```

If the fingerprints differ, the private key pasted into the Jenkins credential is not the counterpart of the `authorized_keys` entry — fix the credential or the entry until they match.

The Deploy stage uses `StrictHostKeyChecking=accept-new`, so the first connection accepts the server's host key automatically; later changes to the host key fail the build (visible rather than silent).

## 6. Point the pipeline at your server

The Deploy stage's target is defined once per Jenkins install in the `Jenkinsfile` `environment` block — edit it to match the values from §1 and §2:

```groovy
DEPLOY_HOST = '172.17.0.1'                        // host reachable *from* the Jenkins agent
DEPLOY_USER = 'hambot-deploy'                     // deploy user created in §1
DEPLOY_DIR = '/opt/hambotv2'                      // server-side clone from §1
DEPLOY_SSH_CREDENTIAL = 'hambot-deploy-ssh-key'   // Jenkins credential ID from §5
```

Notes on `DEPLOY_HOST`:

- `172.17.0.1` is the default Docker bridge gateway — it works when Jenkins runs *in a container* and the production stack runs *on the same host*.
- Alternatively use `host.docker.internal` (requires the container to be started with `--add-host=host.docker.internal:host-gateway`, or the corresponding `extra_hosts` compose entry).
- If Jenkins and production are separate machines, use the production host's address/hostname.

## 7. Create the Jenkins job

1. New Item → **Multibranch Pipeline** (recommended: it discovers branches and PRs automatically and matches the pipeline's tag condition), or a plain **Pipeline** job if you only build `master` and tags.
2. Point it at this repository (Git). For a Multibranch Pipeline this goes in *Branch Sources*.
3. Set **Script Path** to `Jenkinsfile` (the default) — the file is at the repository root.
4. **Enable builds of tag refs** — deploys are gated on version tags (`v<major.minor.patch>`), so the job must build tags or a pushed tag deploys nothing:
   - *Multibranch Pipeline*: in the Git *Branch Source*, *Add property* → **Tag discovery** → *All*.
   - *Plain Pipeline job*: add a refspec `+refs/tags/*:refs/remotes/origin/tags/*` and a branch specifier `origin/tags/*` (or `refs/tags/*`) alongside `*/master`.
5. If Jenkins itself runs in a Docker container and the repository/agent layout needs it, give the container access to the host Docker socket or run the build on an agent with the SDK installed.

## 8. Run it and verify

Push a branch → Jenkins builds and tests it, no deploy. Push (or merge) to `master` → build and test, still no deploy. Push a version tag (e.g. `git tag v1.0.0 && git push origin v1.0.0`) → build, test, then deploy to production.

Verify a deploy from the server:

```bash
cat /opt/hambotv2/.deploy-info   # written by scripts/deploy.sh on success
docker compose logs -f bot       # should show the startup lines, then connect to Discord
```

The pipeline only ever checks out a commit in the server-side clone and runs the repo's own deploy script — it transfers no files and no secrets.

## 9. Stopping the stack

Both services run with `restart: unless-stopped`: crashes and reboots bring them back automatically, but an **explicit stop sticks** — a stopped container stays down across reboots until you start it again. All commands run in the deploy directory (`/opt/hambotv2`) as a user with Docker access (e.g. `sudo -u hambot-deploy`).

```bash
# stop only the bot (database keeps running; bot stays down across reboots)
docker compose stop bot

# stop everything (bot + database)
docker compose stop

# bring it back
docker compose start          # or: docker compose up -d
```

If you also want the containers *removed* (not just stopped):

```bash
docker compose down           # stops and removes containers; db-data volume is KEPT
docker compose down -v        # ALSO DELETES the database data — destructive
```

Caveats:

- **The next Jenkins deploy starts whatever you stopped.** The Deploy stage runs `docker compose up -d`, which recreates and starts stopped services. If the bot must stay down (e.g. maintenance), disable the Jenkins job for that period, not just the container.
- `docker compose down` without `-v` keeps the `db-data` volume, so quotes, permissions, and reminders survive; with `-v` they are gone permanently.
- Stopping the **database** alone is possible (`docker compose stop db`), but pointless for maintenance — the bot's `depends_on: service_healthy` will keep it from starting (or restart-loop on an unhealthy db) until Postgres is back.

## 10. The weekly dependency security scan job

A second, **scheduled** pipeline runs the weekly dependency security scan: it inventories what is actually deployed (per-project NuGet SBOMs plus the Docker image) and checks it against known CVEs. It **never deploys and never touches the running stack** — it has no Deploy stage, and the job deliberately holds no deploy or bot credentials (FR-121). Requirements: FR-121–FR-126 / NFR-27–NFR-28 in [`software-requirements.md`](software-requirements.md); design: §5.3 in [`software-design.md`](software-design.md).

### What the scan pipeline does

| Stage | What happens |
|---|---|
| **Build** | Same command and warning policy as the main pipeline's Build stage — the scan sees the tree exactly as CI builds it. No tests. |
| **SBOM** | Installs the pinned `dotnet CycloneDX` tool and Trivy **into the workspace** (no global agent state, cleaned with the workspace) and generates one CycloneDX JSON SBOM per project: `sbom.hambotv2.cdx.json` (the deployed image's NuGet package set) and `sbom.bottests.cdx.json` (informational). |
| **Image** | `docker build` + `docker save` the deployment image under a scan-only tag — the only way to cover what the NuGet graph cannot see: the ASP.NET Core runtime base layers and the apt-installed `postgresql-client-17`. The image is never pushed, never started, and removed in `post`. |
| **Scan** | Trivy scans both SBOMs and the saved image (`--input`, no Docker socket access needed), emitting one JSON CVE report per input plus the image's own CycloneDX SBOM (`sbom.image.cdx.json`). All scans run with `--exit-code 0`, so findings never fail a stage — but a tool or vulnerability-DB failure does fail the build, so a broken scan is red, not silent. |
| **Report** | Prints a per-report severity summary and the list of high/critical findings to the build output, and applies the result policy (below). |

Artifacts archived per build under `security/`: the three SBOMs, the three CVE reports, and the deployed-tree high/critical count.

### Result policy

- **Low/medium findings**: informational only — the build stays green.
- **HIGH or CRITICAL in the deployed tree** (the `HambotV2` SBOM or the image scan): the build is marked **UNSTABLE — never FAILED** — so the yellow badge is visible in the Jenkins UI without blocking history or re-runs.
- **`BotTests` findings**: archived and reported, but never affect the build result (test-only packages never reach the deployed image).
- There is no Discord/notification-channel route: the pipeline deliberately holds no bot credentials, so findings surface through the Jenkins build status, the build-output summary, and Jenkins email if the controller is configured for it.

### Creating the job

1. New Item → **Pipeline** (a plain pipeline job, *not* multibranch — the scan always checks out `master` on a schedule and builds no tags).
2. **Pipeline** → *Definition*: "Pipeline script from SCM" → point it at this repository (same git credentials as the main job) → **Script Path**: `Jenkinsfile.security`.
3. **Build Triggers**: nothing — the cron trigger lives in the pipeline itself (`0 4 * * 0`, Sundays 04:00). Jenkins evaluates cron in the **controller's timezone**: FR-121 specifies 04:00 UTC, so run the controller on UTC or adjust the expression. The trigger only registers after the pipeline has run once — kick a first build manually after creating the job.
4. **Do not attach any credentials to this job.** No deploy SSH key, no bot token — the scan is isolated by construction (FR-121).
5. **Discard old builds**: enable it and keep the **artifacts** for at least ~26 builds (six months of weekly history — enough to see when a vulnerability entered or left the tree, FR-125); build logs can rotate earlier.

Run the job once manually and verify: the five stages run, the `security/` artifacts appear under the build, the build-output summary lists severities, and (unless the tree is already clean) the result matches the policy. That live run is the feature's acceptance verification (NFR-28) — after it, remove the **(planned)** marker from design §5.3 and fold the as-built behavior into the docs per [`roadmap.md`](roadmap.md).

### Keeping the scan current

The tool versions are pinned in `Jenkinsfile.security`'s `environment` block so results stay comparable week over week: bump `CYCLONEDX_VERSION` / `TRIVY_VERSION` deliberately (a new Trivy version also refreshes its vulnerability DB on first run, cached in the agent user's home). The vulnerability DB downloads automatically from `ghcr.io` on the first scan after each DB update — no registration or interaction.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `dotnet: not found` in the Build stage | SDK not on the agent's path as configured in `environment` — check `DOTNET_ROOT`/`PATH` in the Jenkinsfile match your install location (§3). |
| Build fails with warning CS86xx | The pipeline treats nullable warnings as errors (`/warnaserror:nullable`); fix the warning in code, don't remove the flag. |
| Deploy stage skipped on a tagged commit | The `when` gate only fires for tag builds (`TAG_NAME` matches `v<major.minor.patch>` in multibranch jobs) or, in plain jobs, when the built commit carries a matching tag — check tag discovery/refspec is configured (§7 step 4) and that the tag matches `v<major.minor.patch>` exactly. |
| `sshagent` step not found | The **SSH Agent** plugin is not installed (§4), or the credential ID doesn't match `DEPLOY_SSH_CREDENTIAL`. |
| SSH connection refused / timeout | `DEPLOY_HOST` not reachable from the Jenkins agent — see the host notes in §6. |
| `Permission denied (publickey,password)` in the Deploy stage | The server rejected the agent's key. In order of likelihood: `authorized_keys` was never created or doesn't contain the key (§2 step 2 — check `sudo ssh-keygen -lf /home/hambot-deploy/.ssh/authorized_keys`); the private key in the Jenkins credential doesn't match (compare fingerprints, §2/§5); bad ownership/modes make sshd ignore the key under `StrictModes` (`.ssh` 700, `authorized_keys` 600, all owned by the deploy user — the auth log says "bad ownership or modes"); or sshd config (`PubkeyAuthentication no`, an `AllowUsers` list). Check `sudo journalctl -u ssh -n 50` on the server for the exact reason — the Jenkins attempt is the line with `rhost=172.17.0.x`. |
| `Permission denied` inspecting `~hambot-deploy/.ssh/` as your admin user | Expected — the deploy home is `750` (§1 pitfall). Use `sudo` for all inspection of the deploy user's files. |
| Deploy fails and Jenkins reports `Deploy verification FAILED` | The bot didn't reach its startup lines in time; the job log includes the last 100 bot log lines, and the previous image was rolled back. Check the bot logs on the server (e.g. a bad token in the server's `.env`). |
| Scan job: `docker: not found` / `Cannot connect to the Docker daemon` in the Image stage | The scan job needs Docker *on the Jenkins agent* (unlike the main pipeline, which only SSHes to the server). If Jenkins runs in a container, both halves are needed: the Docker CLI inside the container (`docker exec -u root <jenkins-container> bash -c "apt-get update && apt-get install -y ca-certificates curl gnupg && install -m 0755 -d /etc/apt/keyrings && curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc && echo \"deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo $VERSION_CODENAME) stable\" > /etc/apt/sources.list.d/docker.list && apt-get update && apt-get install -y docker-cli"`) and the host socket mounted (`-v /var/run/docker.sock:/var/run/docker.sock` on the Jenkins container — recreate it with the flag). The socket makes that CLI talk to the *host* daemon; that access is effectively root-equivalent, the same accepted trade-off as §1. Verify with `docker version` inside the Jenkins container before re-running. |
| Scan job: `jq: not found` in the Report stage | Install `jq` on the agent (e.g. `apt-get install -y jq`) — it parses the CVE reports for the summary and the result policy. |
| Scan job fails downloading the Trivy vulnerability DB | The agent needs outbound access to `ghcr.io`. The DB is cached in the agent user's home (`~/.cache/trivy`); a stale cache after an interrupted download is the usual fix — delete the directory and re-run. |
| Scan job: `dotnet-CycloneDX` fails to find a .NET runtime | The pipeline sets `DOTNET_ROLL_FORWARD=Major` so the pinned SBOM tool (built for an older runtime) runs on the agent's .NET 10 — check `DOTNET_ROOT`/`PATH` still point at the SDK install (§3). |
| Scan job never runs on schedule | The cron trigger registers only after the pipeline has run once from the job — run one manual build after creating the job, and remember the expression is evaluated in the controller's timezone (§10). |
| Scan build UNSTABLE but nothing seems broken | That is the result policy working: a HIGH/CRITICAL finding in the deployed tree marks the build UNSTABLE by design (§10). The build-output summary lists the findings; the fix is updating the affected package, not re-running. |
| Host key verification failed after server reinstall | `accept-new` only accepts *new* keys; remove the stale entry from the deploy user's `known_hosts`. |
