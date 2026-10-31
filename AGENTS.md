# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project Overview

HambotV2 is a Discord chat bot on .NET 10 (`net10.0`): Discord.Net 3.20.2 for the gateway, EF Core 10 / Npgsql on PostgreSQL, generic host (Worker SDK) with an in-process Kestrel MCP endpoint (`ModelContextProtocol.AspNetCore`), MSTest + Moq tests in `BotTests/` (which has `InternalsVisibleTo` into the main project).

## Build & Test

```bash
dotnet build HambotV2.sln
dotnet test HambotV2.sln
```

Tests run without a database or Discord token. The build is warning-free; keep it that way.

## Architecture (details in docs/software-design.md)

- Entry point `HambotV2/Program.cs`: registers `DatabaseContext` (transient Npgsql), hosted services (the `DiscordClient` gateway, the reminder delivery engine in `Scheduling/`, the quote-of-the-day loop in `QuoteOfTheDay/`, the `Watchdog/` health-check loop, the opt-in `Mcp/` endpoint, the `Llm/` presence/bio update loop), and the command pipeline.
- Message flow: `Connections/DiscordClient.cs` forwards messages starting with the guild's effective command identifier to `ICommandHandler` (`Commands/CommandHandler.cs`), which checks bot bans (`Commands/BotBans.cs`), resolves the command via `ICommandService.TryGetCommand`, checks permissions (`PermissionsService`), then executes with a `CommandContext` wrapping a `DatabaseContext`.
- Gateway intents are `Guilds | GuildMessages | DirectMessages | MessageContent` — add new intents explicitly, never switch back to `AllUnprivileged` (it warns).
- To add a command: implement `Commands/ICommand.cs` (`Name`, `Description`, `Usage`, `DefaultPermissionTier`, `Category` + `ExecuteCommand`), then register it in the relevant `Commands/Register*Commands.cs` class (system commands in `Commands/SystemCommands/`, LLM commands in `Commands/LlmCommands/`).
- Command names must be lowercase; registration rejects commands missing help text or category — the registration-contract test enforces this for every command, so don't re-assert it per command in tests.
- Permissions are tiered integers (`PermissionsService`): user tier >= command tier. Tiers: 0 everyone, 1 moderator, 2 admin, 3 owner (guild owner), 4 bot owner (config only). Managed at runtime via `!commandpermission` / `!userpermission`.
- Data model (`Connections/DatabaseContext.cs`): `User`, `CommandMetaData`, `GuildSetting`, `Quote`, `CustomResponse` (per guild), `Reminder` (per user), `BotSetting` (global), `BotBan` (per guild). Migrations live in `HambotV2/Migrations/`; regenerate with `dotnet ef migrations add <Name>` from `HambotV2/` (the `DesignTimeDatabaseContextFactory` avoids running startup).

## Configuration & Secrets

- `appsettings.json`: non-secret config. `HambotV2/appsecrets.json` (gitignored): `Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, and the optional `Mcp` and `Llm` (`Llm:ApiKey`) sections. Never commit secrets.
- Startup validates required keys and applies migrations automatically; failures exit non-zero with a critical log line.

## Docker

Build context is the repo root. Recommended: `docker-compose.yml` (bot + PostgreSQL + backup sidecar); configure `.env` from `.env.example`. The image is secret-free (`appsecrets.json` and `.env` are `.dockerignore`d).

## Git

- **Do not stage or commit anything to git yourself.** Leave all changes unstaged in the working tree; the user handles version control.

## Conventions

- C# with nullable reference types and implicit usings; keep new bot-internal types `internal`.
- DI lifetimes: `CommandService` singleton; `CommandHandler`, `PermissionsService`, `DatabaseContext` transient (handler resolved per message, disposed after use).
- Code style: let the code speak — descriptive names over comments; comments only for non-obvious constraints/whys. No FR-xx/§-spec references in code, tests, or comments (traceability lives in `docs/software-requirements.md` and `docs/software-design.md`). Keep XML docs to one or two lines. Don't fragment code into single-use one-line helpers; don't duplicate shared logic.
- Tests assert behavior once; wait for persisted end state, not intermediate signals. Name tests for the observable behavior they verify.
- Docs: `docs/software-design.md` (as-built design; **(planned)** sections are roadmap), `docs/software-requirements.md` (FR/NFR), `docs/roadmap.md` (outstanding work only — remove tasks when signed off and fold as-built behavior into design/requirements), `docs/manual-test-plan.md`, `docs/jenkins-cicd.md`.
- Doc style (see `docs/software-design.md` as the reference): lead with structure, not prose — ASCII diagrams in code fences for flows/topologies/sequences, tables for catalogs (commands, entities, config keys, DI lifetimes); prose only for rules that cannot be tabulated, one line per rule. Concise but precise: keep file paths, defaults, enum values, and FR refs — never drop a fact to save words.
- Keep section numbering in `docs/software-design.md` stable: `§x.y` numbers are cross-referenced from code comments, `docker-compose.yml`, the Jenkinsfiles, and `docs/roadmap.md`. Add new sections at the end of their parent group; renumber only with a repo-wide reference sweep.
