# Jenkins CI/CD Setup

How to wire this repository up to [Jenkins](https://www.jenkins.io/) so that every push is built, tested, and — on `master` — automatically deployed to the production server.

The pipeline lives in the repository root [`Jenkinsfile`](../Jenkinsfile) and is meant to be used as **"Pipeline script from SCM"**, so the pipeline definition stays versioned alongside the code it builds.

## What the pipeline does

| Stage | Runs on | What happens |
|---|---|---|
| **Build** | every build | `dotnet build HambotV2.sln --configuration Release /warnaserror:nullable` — the build is warning-free; this fails the job if a commit introduces a nullable warning. |
| **Test** | every build | `dotnet test HambotV2.sln --no-build` with results written as TRX files to `TestResults/`. Tests need no database, Discord token, or Docker. |
| **Deploy** | `master` only | Jenkins SSHes into the production host, checks out the exact commit CI just built and tested in the deploy clone, and runs [`scripts/deploy.sh`](../scripts/deploy.sh). Branch/PR builds stop after Test. |

Safety properties:

- A stage only runs if the previous ones succeeded — **a failed build or test run never deploys anything**.
- The deploy script builds the new Docker image *before* replacing any container, so a failed image build leaves the running stack untouched.
- After starting the new container, the script waits for the database healthcheck and the bot's known startup lines (`Database migrations applied successfully`, `Discord gateway connection established.`); if they don't appear in time, it rolls back to the previously running image and the Jenkins job fails.
- Secrets never pass through Jenkins: the production `.env` (gitignored) lives on the server next to `docker-compose.yml` and is read by Compose there.

## Prerequisites

- A Jenkins controller/agent running **Linux** (the pipeline uses `sh`).
- The **.NET 10 SDK** (`10.0.x`) on the agent, expected at `/opt/dotnet10` (see below — adjust the `environment` block if you install elsewhere).
- Jenkins plugins: **Git**, **Pipeline**, and **SSH Agent** (for the Deploy stage). Optionally an xUnit/TRX publisher plugin to render test results in the UI.
- Git credentials in Jenkins for this repository (or a public repo).
- For deployment: a reachable production host with Docker + the Compose plugin, git, and the repository cloned there (see [Server-side setup](#server-side-setup)).

## 1. Install the .NET 10 SDK on the agent

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

## 2. Install the required Jenkins plugins

Manage Jenkins → Plugins → Available:

- **Pipeline** (usually preinstalled) — declarative pipeline support.
- **Git** (usually preinstalled) — checkout and "Pipeline script from SCM".
- **SSH Agent** — provides the `sshagent` step used by the Deploy stage.
- Optional: **Publish Test results** / an xUnit plugin to render the TRX files nicely (the pipeline archives `TestResults/**/*.trx` either way, so results are inspectable even without one).

## 3. Create the Jenkins job

1. New Item → **Multibranch Pipeline** (recommended: it discovers branches and PRs automatically and matches the pipeline's `branch 'master'` condition), or a plain **Pipeline** job if you only build `master`.
2. Point it at this repository (Git). For a Multibranch Pipeline this goes in *Branch Sources*.
3. Set **Script Path** to `Jenkinsfile` (the default) — the file is at the repository root.
4. If Jenkins itself runs in a Docker container and the repository/agent layout needs it, give the container access to the host Docker socket or run the build on an agent with the SDK installed.

## 4. Configure the deploy target

The Deploy stage's target is defined once per Jenkins install in the `Jenkinsfile` `environment` block — edit it to match your server:

```groovy
DEPLOY_HOST = '172.17.0.1'                        // host reachable *from* the Jenkins agent
DEPLOY_USER = 'hambot-deploy'                     // SSH user on that host
DEPLOY_DIR = '/opt/hambotv2'                      // server-side clone of this repository
DEPLOY_SSH_CREDENTIAL = 'hambot-deploy-ssh-key'   // Jenkins credential ID (see below)
```

Notes on `DEPLOY_HOST`:

- `172.17.0.1` is the default Docker bridge gateway — it works when Jenkins runs *in a container* and the production stack runs *on the same host*.
- Alternatively use `host.docker.internal` (requires the container to be started with `--add-host=host.docker.internal:host-gateway`, or the corresponding `extra_hosts` compose entry).
- If Jenkins and production are separate machines, use the production host's address/hostname.

### Jenkins credential

The deploy key pair and the `authorized_keys` entry are created once as part of the [server-side setup](#server-side-setup-one-time-not-part-of-the-pipeline). In Jenkins itself: Manage Jenkins → Credentials → add **SSH Username with private key**, paste the *private* key, and give it the ID used in `DEPLOY_SSH_CREDENTIAL` (`hambot-deploy-ssh-key` by default — pick your own ID and update the Jenkinsfile if you prefer).

The stage uses `StrictHostKeyChecking=accept-new`, so the first connection accepts the server's host key automatically; later changes to the host key fail the build (visible rather than silent).

## Server-side setup (one-time, not part of the pipeline)

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
- SSH reachability for the Jenkins container is covered under [Configure the deploy target](#4-configure-the-deploy-target) (`DEPLOY_HOST`).

### Jenkins side

Also once, on the Jenkins side. Generate the SSH key pair for the deploy user (no passphrase — the pipeline must use it non-interactively), install the public key on the production host, then register the private key in Jenkins:

```bash
# 1. Generate the key pair as the deploy user (run on the production host,
#    or generate anywhere and copy the files over)
sudo -u hambot-deploy ssh-keygen -t ed25519 -f ~hambot-deploy/.ssh/id_ed25519 -N "" -C "jenkins-deploy"

# 2. Authorize it for the deploy user (id_ed25519 -> authorized_keys)
sudo -u hambot-deploy sh -c 'cat ~hambot-deploy/.ssh/id_ed25519.pub >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys'

# 3. Print the private key and copy it for the Jenkins credential in step 4
sudo -u hambot-deploy cat ~hambot-deploy/.ssh/id_ed25519
```

Then in Jenkins: Manage Jenkins → Credentials → add **SSH Username with private key**, paste the private key, set the username to `hambot-deploy` (i.e. `DEPLOY_USER`), and give the credential the ID used in `DEPLOY_SSH_CREDENTIAL` (`hambot-deploy-ssh-key` by default).

- Jenkins needs the **SSH Agent** plugin; set `DEPLOY_HOST`/`DEPLOY_USER`/`DEPLOY_DIR`/`DEPLOY_SSH_CREDENTIAL` in the Jenkinsfile `environment` block accordingly (see [Configure the deploy target](#4-configure-the-deploy-target)).
- The Jenkins container must reach the host's `sshd`: use the host's IP, the docker bridge gateway (`172.17.0.1`), or add `extra_hosts: ["host.docker.internal:host-gateway"]`.

The pipeline only ever checks out a commit in this clone and runs the repo's own deploy script — it transfers no files and no secrets.

## 5. Run it

Push a branch → Jenkins builds and tests it, no deploy. Push (or merge) to `master` → build, test, then deploy to production.

Verify a deploy from the server:

```bash
cat /opt/hambotv2/.deploy-info   # written by scripts/deploy.sh on success
docker compose logs -f bot       # should show the startup lines, then connect to Discord
```

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `dotnet: not found` in the Build stage | SDK not on the agent's path as configured in `environment` — check `DOTNET_ROOT`/`PATH` in the Jenkinsfile match your install location. |
| Build fails with warning CS86xx | The pipeline treats nullable warnings as errors (`/warnaserror:nullable`); fix the warning in code, don't remove the flag. |
| Deploy stage skipped on a plain Pipeline job | For non-multibranch jobs the condition relies on `env.GIT_BRANCH == 'origin/master'` — make sure the job builds the `master` branch. |
| `sshagent` step not found | The **SSH Agent** plugin is not installed (or the credential ID doesn't match `DEPLOY_SSH_CREDENTIAL`). |
| SSH connection refused / timeout | `DEPLOY_HOST` not reachable from the Jenkins agent — see the host notes above. |
| Deploy fails and Jenkins reports `Deploy verification FAILED` | The bot didn't reach its startup lines in time; the job log includes the last 100 bot log lines, and the previous image was rolled back. Check the bot logs on the server (e.g. a bad token in the server's `.env`). |
| Host key verification failed after server reinstall | `accept-new` only accepts *new* keys; remove the stale entry from the deploy user's `known_hosts`. |
