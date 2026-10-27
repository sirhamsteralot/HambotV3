# Jenkins CI/CD Setup

How to wire this repository to Jenkins so every push is built and tested, and version tags (`v<major.minor.patch>`) auto-deploy to production. The pipeline lives in the root [`Jenkinsfile`](../Jenkinsfile), used as **"Pipeline script from SCM"**.

## What the pipeline does

| Stage | Runs on | What happens |
|---|---|---|
| **Build** | every build | `dotnet build HambotV2.sln --configuration Release /warnaserror:nullable` |
| **Test** | every build | `dotnet test HambotV2.sln --no-build` (TRX to `TestResults/`); no database, token, or Docker needed |
| **Deploy** | version tags only | SSH to the production host, check out the exact built commit in the deploy clone, run [`scripts/deploy.sh`](../scripts/deploy.sh) |

Safety properties: a failed build/test never deploys; `deploy.sh` builds the new image *before* replacing containers, verifies the startup lines (`Database migrations applied successfully`, `Discord gateway connection established.`, `Bot version: <version>` matching tag + commit), and rolls back to the previous image if they don't appear; secrets never pass through Jenkins (the server's gitignored `.env` is read by Compose there).

A second scheduled pipeline, [`Jenkinsfile.security`](../Jenkinsfile.security), runs the weekly dependency security scan — see §10.

## Setup steps (in order)

### 1. Prepare the production server

```bash
# docker group membership is effectively root-equivalent — accepted for this single-server deploy
sudo useradd -m hambot-deploy && sudo usermod -aG docker hambot-deploy
sudo mkdir -p /opt/hambotv2 && sudo chown hambot-deploy /opt/hambotv2
sudo -u hambot-deploy git clone <origin-url> /opt/hambotv2   # deploy key if the repo is private
cd /opt/hambotv2 && sudo -u hambot-deploy cp .env.example .env  # fill in; the pipeline never touches this file
```

Note: `useradd -m` creates a mode-`750` home — correct for sshd's `StrictModes`, but your admin account can't read the deploy user's files; inspect them with `sudo`.

### 2. Create and authorize the deploy SSH key (on the server)

```bash
sudo -u hambot-deploy mkdir -p ~hambot-deploy/.ssh && sudo chmod 700 ~hambot-deploy/.ssh
sudo -u hambot-deploy ssh-keygen -t ed25519 -f ~hambot-deploy/.ssh/id_ed25519 -N "" -C "jenkins-deploy"
sudo -u hambot-deploy sh -c 'cat ~/.ssh/id_ed25519.pub >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys'
# Verify BOTH halves exist (the most common deploy failure is a skipped authorized_keys step):
sudo ls -la /home/hambot-deploy/.ssh/                        # id_ed25519, id_ed25519.pub, authorized_keys
sudo ssh-keygen -lf /home/hambot-deploy/.ssh/authorized_keys  # the fingerprint sshd will accept
sudo -u hambot-deploy cat ~hambot-deploy/.ssh/id_ed25519     # copy the private key for step 5
```

If `authorized_keys` is missing, sshd falls through to password auth and Jenkins fails with `Permission denied (publickey,password)`. Wrong ownership/modes make sshd ignore the key *silently* (`.ssh` 700, `authorized_keys` 600, both owned by the deploy user).

### 3. Install the .NET 10 SDK on the agent

The Jenkinsfile expects `DOTNET_ROOT = '/opt/dotnet10'` (and that on `PATH`) — install there, symlink, or edit the `environment` block. Verify: `/opt/dotnet10/dotnet --version` → `10.0.x`.

### 4. Install Jenkins plugins

**Pipeline** and **Git** (usually preinstalled), **SSH Agent** (the Deploy stage's `sshagent` step), optionally a test-results plugin for the TRX files.

### 5. Add the Jenkins credentials

- **Deploy SSH key**: "SSH Username with private key", paste the private key from §2, username `hambot-deploy`, ID matching `DEPLOY_SSH_CREDENTIAL`.
- **Git** (private repos only): the PAT/deploy token for checkout.

Verify the halves match: compare the authorized fingerprint (§2) with the key the agent offers (`ssh -v hambot-deploy@<host> 2>&1 | grep 'offering public key'`). The Deploy stage uses `StrictHostKeyChecking=accept-new` — first connect accepts, later host-key changes fail visibly.

### 6. Point the pipeline at your server

Edit the `Jenkinsfile` `environment` block:

```groovy
DEPLOY_HOST = '172.17.0.1'                        // reachable from the Jenkins agent; 172.17.0.1 is the Docker
                                                 // bridge gateway when Jenkins is containerized on the host
DEPLOY_USER = 'hambot-deploy'
DEPLOY_DIR = '/opt/hambotv2'
DEPLOY_SSH_CREDENTIAL = 'hambot-deploy-ssh-key'
```

### 7. Create the Jenkins job

1. New Item → **Multibranch Pipeline** (or a plain Pipeline job for `master` + tags only).
2. Point it at this repository; **Script Path**: `Jenkinsfile`.
3. **Enable builds of tag refs** — deploys are gated on tags: Multibranch → Branch Source → *Add property* → **Tag discovery** → All; plain Pipeline → refspec `+refs/tags/*:refs/remotes/origin/tags/*` + specifier `origin/tags/*` alongside `*/master`.

### 8. Run it and verify

Push a branch or `master` → build + test, no deploy. Push a tag (`git tag v1.0.0 && git push origin v1.0.0`) → build, test, deploy. Verify on the server: `cat /opt/hambotv2/.deploy-info`, `docker compose logs -f bot`.

### 9. Stopping the stack

An explicit stop sticks across reboots (`restart: unless-stopped`). Run in `/opt/hambotv2` as a Docker-access user:

```bash
docker compose stop bot    # bot only; or `stop` for everything; `start`/`up -d` brings it back
docker compose down       # also removes containers; db-data volume KEPT
docker compose down -v    # ALSO DELETES the database data — destructive
```

The next Jenkins deploy starts whatever you stopped (`docker compose up -d`) — disable the Jenkins job if the bot must stay down. Stopping only the db is pointless: the bot waits on the db healthcheck.

## 10. The weekly dependency security scan job

A scheduled Pipeline job (cron `0 4 * * 0` in the pipeline itself; controller timezone — FR-121 specifies 04:00 UTC). It inventories the deployed tree (per-project NuGet SBOMs + the Docker image) and checks it for CVEs. It **never deploys** and holds no deploy or bot credentials.

Stages: **Build** (same policy as the main pipeline, no tests) → **SBOM** (pinned `dotnet CycloneDX` + jq installed into the workspace) → **Image** (`docker build` + `docker save`, never pushed or started) → **Scan** (Trivy over SBOMs and the saved tarball; findings never fail a stage, tool/DB failures do) → **Report** (severity summary; HIGH/CRITICAL in the deployed tree → build **UNSTABLE**, never FAILED; `BotTests` findings informational).

Creating the job: New Item → **Pipeline** (plain, not multibranch) → "Pipeline script from SCM" → Script Path `Jenkinsfile.security` → no credentials attached → Discard old builds with artifacts kept ≥ ~26 builds. **Run it once manually** — that is what registers the cron trigger and serves as the feature's acceptance verification (NFR-28).

Tool versions are pinned in `Jenkinsfile.security`; bump them deliberately. The Image stage needs Docker *on the Jenkins agent* — with a containerized Jenkins, mount the host socket and a Docker CLI as volumes in the Jenkins compose file.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `dotnet: not found` in Build | `DOTNET_ROOT`/`PATH` in the Jenkinsfile don't match the agent install (§3). |
| Nullable warning CS86xx fails the build | By design (`/warnaserror:nullable`); fix the warning. |
| Deploy skipped on a tag | Tag discovery/refspec not configured (§7.4), or the tag doesn't match `v<major.minor.patch>`. |
| `sshagent` step not found | SSH Agent plugin missing, or credential ID ≠ `DEPLOY_SSH_CREDENTIAL`. |
| `Permission denied (publickey,password)` | `authorized_keys` missing/incomplete (§2), key mismatch (compare fingerprints), or bad ownership/modes silently ignored by sshd — check `sudo journalctl -u ssh -n 50`. |
| `Deploy verification FAILED` | The bot didn't reach its startup lines (e.g. bad token in the server `.env`); the old image was rolled back — check the bot logs. |
| Scan: `docker: not found` / daemon unreachable | Mount the host socket + CLI into the Jenkins container as volumes; verify with `docker version` inside (name the container explicitly). |
| Scan: `jq: not found` | The pinned jq download in the SBOM stage failed — check outbound access to `github.com` releases. |
| Scan: Trivy DB download fails | The agent needs outbound access to `ghcr.io`; delete a stale `~/.cache/trivy` and re-run. |
| Scan never runs on schedule | The cron registers only after the first manual run; also check the controller timezone. |
| Scan UNSTABLE with nothing "broken" | The result policy working — a HIGH/CRITICAL finding in the deployed tree; fix the package, don't re-run. |
| Host key verification failed after a server reinstall | `accept-new` only accepts *new* keys — remove the stale `known_hosts` entry. |
