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
- **Version reporting** (FR-76–FR-77 / NFR-17 / C-5; design §3.9) — the build embeds the release tag + commit hash and the bot reports it via `!version` and its startup log line.

No work is currently planned beyond the task below's remaining operator action.

- [x] **Version reporting — `!version`** (FR-76–FR-77 / NFR-17 / C-5; design §3.9)

    Implemented: the MSBuild target in `HambotV2.csproj` embeds the raw release tag and commit hash as assembly metadata, the `BotVersion` provider formats them (pure, unit-tested logic with the FR-76 fallbacks), `Program` logs `Bot version: <version>` once at startup, and the `!version` command (tier 0, works in DMs, help text) replies with it. Every deployment mode is wired: source and Jenkins builds read git for free, `Dockerfile`/`docker-compose.yml`/`scripts/deploy.sh` pass the pieces in, and the deploy verification requires the startup version line to match the deployed tag + commit.

    **Remaining operator action**: create the first release tag (`v1.0.0`) so the reported version is meaningful — and, under the tag-only deploy policy, trigger the first deploy (C-5).
