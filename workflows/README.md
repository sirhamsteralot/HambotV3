# Hambot workflow worker

Mistral Workflows worker for the HambotV3 repository.

## `hambot-cve-fix-pr`

Runs weekly on **Mondays 06:00 UTC** (schedule on the deployment; the Jenkins
security scan itself runs Sundays 04:00 — see `Jenkinsfile.security` /
`docs/jenkins-cicd.md` §10 — so Monday morning picks up the fresh report). Per run it:

1. Uses the **Jenkins MCP connector** (`jenkins_a59a`, `run_as="deployment"`) to
   find the security scan job for this repo (`findJobsWithScmUrl` on the repo
   URL, then `getJobScm` to pick the job whose script path is
   `Jenkinsfile.security`; override with the `jenkins_job` input).
2. Reads the last completed build of that job (`getJob`/`getBuild`) and parses
   the Report-stage lines of its log (`getBuildLog` tail, `searchBuildLog`
   fallback) for `CRITICAL <CVE> <pkg> <installed> -> fixed <version>` findings.
3. If there are no CRITICAL findings, the run ends with the scan summary.
4. Otherwise it clones this repo, bumps every direct `PackageReference` whose
   scanner-reported fixed version differs (highest fixed version wins), writes
   a `docs/security/cve-fix-report-<yyyymmdd>.md` summary, pushes a
   `fix/critical-cves-*` branch, and opens a **draft PR** with the CVE table and
   the suggested fix per finding. Findings without a fixed version, or in
   transitive / base-image packages, are listed as manual-remediation notes.

### Inputs (`CveInput`)

- `dry_run` (default `false`): collect + plan only, no push / PR.
- `repo_url`, `base_branch` (`master`), `branch_prefix`: defaults target this repo.
- `jenkins_job`: optional explicit Jenkins full job path; empty = auto-discover via SCM URL.

### Credentials

- **Jenkins**: the unattended scheduled run resolves connector credentials from
  the deployment identity, so `jenkins_a59a` needs a **workspace-scope
  credential** holding a Jenkins API token (Overall/Read, View/Read, Job/Read),
  added in Studio › Connectors › `jenkins_a59a` › Credentials. A user-scope
  credential is not enough (Jenkins would answer "anonymous").
- **GitHub**: a workspace secret holding a PAT with repo contents + pull
  requests write on `sirhamsteralot/HambotV3`, bound on the deployment as
  `HAMBOT_GITHUB_TOKEN` (the existing `hee_github_token` secret also works via
  the fallback spelling, if its scopes cover this repo).

## Local development

```sh
uv sync
uv run pytest tests/          # workflow + helper tests (in-memory Temporal)
```

Worker entrypoint: `apps/worker/src/worker/entrypoints/worker.py`.
Deployment image: `deploy/docker/Dockerfile.worker` (build context: this
`workflows/` directory; managed deployment: build directory `workflows`,
dockerfile `deploy/docker/Dockerfile.worker`, repository
`https://github.com/sirhamsteralot/HambotV3`).
