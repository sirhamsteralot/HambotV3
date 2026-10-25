# Roadmap

Planned work for HambotV2. Each task is fully specified in [`software-requirements.md`](software-requirements.md) and designed in [`software-design.md`](software-design.md) before implementation starts.

## How to use this file

For each task:

- **Problem**: what is wrong or missing today.
- **Steps**: the ordered implementation checklist — take them top to bottom; each step leaves the solution building and tests green.
- **Acceptance criteria**: what defines "done" (mirrored from the requirements).

Tick a task off (`- [x]`) when complete and fold the resulting behavior into the design and requirements documents so they keep describing the system as it actually is — in particular, remove the *(planned)* markers from the feature's design section.

## Status

All previously planned work is implemented and in production:

- **Quotes** — quote storage, recall, search, and legacy import (FR-42–FR-50, FR-72–FR-75; design §3.6).
- **Custom command responses** (FR-51–FR-60; design §3.7).
- **Reminders** (FR-61–FR-71; design §3.8).
- **Jenkins auto-deploy** (NFR-6/NFR-8; design §5.1) — version tags (`v<major.minor.patch>`) that build and test cleanly deploy automatically; ordinary pushes (including `master`) are built and tested only.

Currently planned work (specified, not yet implemented):

- [ ] **Version reporting — `!version`** (FR-76–FR-77 / NFR-17 / C-5; design §3.9)

    **Problem**: The running bot exposes no version information. Operators can only tell which commit is deployed by inspecting the server (image tags, deploy logs), and users have no way at all to see what version they are talking to — no command shows it.

    **Steps**:

    1. Define the version string and embed it at build time: MSBuild target in `HambotV2.csproj` computing `BotVersion` from git — `v`-prefixed tag semver + 7-char commit hash, fallbacks `0.0.0+<hash>` (untagged) and `0.0.0+unknown` (no git) — feeding `Version`/`InformationalVersion` (FR-76, design §3.9). Nothing consumes it yet; the build stays green.
    2. Add the runtime `BotVersion` provider reading the embedded version, with the tag→version formatting extracted as pure, unit-tested string logic (NFR-17); log the version once at startup (design §3.9).
    3. Add the `!version` command (`Commands/SystemCommands/`, tier 0, works in DMs, help text) and register it in `RegisterSystemCommands` (FR-77); unit tests for registration and reply.
    4. Wire the deployment paths so every build mode reports the right version (design §3.9 "Deployment wiring"): `Dockerfile` `ARG BOT_VERSION` + `-p:BotVersion`, `docker-compose.yml` `build.args`, `scripts/deploy.sh` computing/exporting the version and verifying it in the startup log, `Jenkinsfile` stamping the tested build identically.
    5. Create the first release tag (`v1.0.0`) so the reported version is meaningful, and document the tagging convention (C-5) — this is also what triggers the bot's first deploy under the tag-only deploy policy.

    **Acceptance criteria**:

    - `!version` replies with the latest tag's semver plus the build commit's short hash (e.g. `1.2.3+1639324`), works in direct messages, defaults to tier 0, and appears in `!help` (FR-77). The bot sets no Discord presence/status — the command is the only user-facing version surface.
    - A Jenkins deploy of a version tag build reports exactly that tag + commit; the deploy verification fails — and rolls back — if the running bot reports a different version.
    - Builds with no reachable tag report `0.0.0+<hash>`; builds without git metadata report `0.0.0+unknown` — never a wrong release number, never a build or startup failure (FR-76).
    - New behavior is covered by unit tests per NFR-17, and the build stays warning-free (NFR-7).
