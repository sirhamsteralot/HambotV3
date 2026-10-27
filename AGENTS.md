# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project Overview

HambotV2 is a Discord chat bot built on .NET 10 (`net10.0`) using:

- **Discord.Net 3.20.2** for the Discord gateway connection
- **EF Core 10 / Npgsql** against a PostgreSQL database
- **Microsoft.Extensions.Hosting** (Worker service / generic host) with an ASP.NET Core framework reference — Kestrel hosts the in-process MCP endpoint
- **ModelContextProtocol.AspNetCore 2.3.0** (the official C# SDK) for the MCP streamable-HTTP surface
- **MSTest + Moq** for unit tests in the `BotTests` project

The solution (`HambotV2.sln`) contains two projects:

- `HambotV2/` — the bot itself (Worker SDK)
- `BotTests/` — unit tests (has `InternalsVisibleTo` into the main project)

## Build & Test

```bash
dotnet build HambotV2.sln
dotnet test HambotV2.sln
```

- Building requires the .NET 10 SDK (`10.0.x`).

- Tests run without a database or Discord token; they use Moq and in-memory configuration. All tests are in `BotTests/` (`TestCommandInfrastructure.cs`, `TestCommands.cs`, `TestPermissions.cs`, `TestCommandHandler.cs`, `TestConfigurationValidator.cs`, `TestPermissionCommands.cs`, `TestHelp.cs`, `TestCommandArguments.cs`, `TestGuildSettings.cs`, `TestCooldowns.cs`, `TestQuotes.cs`, `TestCustomResponses.cs`, `TestReminders.cs`, `TestVersion.cs`, `TestBackups.cs`, `TestWatchdog.cs`, `TestPresence.cs`, `TestProfileImages.cs`, `TestMcp.cs`, `TestAgentTier.cs`, `TestBotBans.cs`, plus the shared `DiscordTestDoubles.cs`).
- The build is warning-free; keep it that way and avoid introducing new warnings.

## Docker

The bot is containerized via `HambotV2/Dockerfile` (multi-stage .NET 10 build → `dotnet publish` → runtime-only final image on the **ASP.NET Core runtime base** — required by the in-process Kestrel MCP endpoint — that installs `postgresql-client-17` from the PostgreSQL apt repository, for the in-bot backup commands). **The build context is the repository root**, not the project directory. The recommended way to run everything (bot + PostgreSQL + backup sidecar) is `docker-compose.yml` at the repo root:

```bash
# configure secrets (see .env.example)
cp .env.example .env   # fill in DISCORD_BOT_TOKEN and POSTGRES_PASSWORD

# build & run bot + database + backup sidecar
docker compose up -d --build
docker compose logs -f bot   # should log "Database migrations applied successfully", then "Initializing Discord connection..."
```

Plain Docker works too, with secrets passed as environment variables (`Secrets__BotToken`, `ConnectionStrings__DefaultConnection`):

```bash
# build (from repo root)
docker build -f HambotV2/Dockerfile -t hambotv2:latest .

# smoke test
docker run -d --name hambot-smoke -e Secrets__BotToken=... -e "ConnectionStrings__DefaultConnection=..." hambotv2:latest
docker logs -f hambot-smoke
```

- Startup applies EF Core migrations automatically and validates required configuration; missing keys or an unreachable database exit with a non-zero code and a critical log line.
- The image is secret-free: `appsecrets.json` and `.env` are excluded from the build context via `.dockerignore` (local dev file loading remains a fallback for source runs).

## Architecture

Entry point is `HambotV2/Program.cs`: a generic host registers `DatabaseContext` (transient Npgsql), the hosted services (the `DiscordClient` gateway connection, the generic scheduled-message delivery engine in `Scheduling/`, hosted for reminders to deliver them as direct messages, the `Watchdog/WatchdogService` health-check loop, and the opt-in `Mcp/McpServerService` — the operator-agent MCP endpoint over streamable HTTP, bearer-key authenticated, with the tool implementations in `Mcp/McpTools.cs` behind the public `IMcpTools` boundary and the `IMcpDiscordClient` seam over the shared client; the agent acts at a per-guild tier, default the lowest, set with the owner-tier `!agenttier` command, design §3.11), the command pipeline, and the stability components: `Maintenance/MaintenanceMode` (gates the pipeline and delivery engine during a backup restore), `Backups/` (`IBackupManager` + status-file reader behind the `!backup`/`!backups`/`!restorebackup` commands), `Watchdog/` (`!health`, `!notifychannel`, the notification publisher), and the bot's Discord identity: `Connections/PresenceService.cs` (singleton `IPresenceService`, the single path for activity/bio/avatar/banner changes, applied on gateway Ready) behind the `Connections/DiscordPresenceGateway.cs` seam, driven by the tier-4 `!presence`/`!bio`/`!avatar`/`!banner` commands with `Commands/SystemCommands/ProfileImages.cs` validation and the `IImageContentDownloader` download abstraction. The `backup` compose service (`scripts/backup/`) dumps the database on schedule into the shared `backups` volume and writes the `.backup-status` file that `!health` and the watchdog consume.

### Message flow

1. `Connections/DiscordClient.cs` (BackgroundService) connects to Discord with the token from `Secrets:BotToken`. Messages starting with the configured `BotConfiguration:CommandIdentifier` (default `!`) are forwarded to `ICommandHandler`.
   - **Gateway intents**: request only what the bot uses — currently `Guilds | GuildMessages | DirectMessages | MessageContent` in `DiscordClient.cs`. Do NOT switch back to `GatewayIntents.AllUnprivileged`: it triggers Discord.Net "using intent without listening to any events" warnings on every connect. When adding a feature that needs a new intent (e.g. reactions, members), add it explicitly and keep this list current.
2. `Commands/CommandHandler.cs` resolves the command via `ICommandService.TryGetCommand` (the command name is the first space-delimited token minus the identifier, lowercased), checks permissions via `IPermissionsService`, then executes the command with a `CommandContext` wrapping the `DatabaseContext`. Before any of that, a per-guild bot-ban check (`Commands/BotBans.cs`, backed by the `BotBan` entity) drops a banned user's invocation silently on both surfaces — the guild owner, the bot owner, and MCP operator-agent invocations are exempt, and expired temporary bans are deleted on sight.
3. Commands implement `Commands/ICommand.cs` (`Name`, `Description`, `Usage`, `DefaultPermissionTier`, `Category` + `ExecuteCommand(IMessage, ICommandContext)`).
4. Opt-in via `SlashCommands:Enabled` (default false), the `SlashCommands/SlashCommandService` singleton exposes the administrative/configuration, user-scoped, and help commands as Discord application commands: it registers definitions with a bulk global overwrite after the gateway connects and executes interactions through the real `CommandHandler` via the shared virtual-invocation seam (`Commands/VirtualCommandRunner.cs` — the same seam the MCP `run_command` tool reuses, design §3.11). Slash eligibility is declared per command via the optional `ICommand.ApplicationCommand` property (`SlashCommands/ApplicationCommandMetadata.cs`); the remaining community-facing commands stay text-only. `!help` is exposed as `/help`, and a multi-page help list carries interactive previous/next buttons: `SlashCommands/HelpPaginationService` (attached unconditionally — the buttons ride on text-surface messages too) turns a click back into a `help <page>` invocation through the same seam and updates the message in place. See `docs/software-design.md` §3.15.

### Command registration

- `Commands/CommandService.cs` is a singleton holding a `Dictionary<string, ICommand>`, lazily populated on first use via `Commands/RegisterAllCommands.cs`.
- To add a command: create a class implementing `ICommand` in an appropriate namespace (system commands live in `Commands/SystemCommands/`), then register it in the relevant `Register*Commands` class (e.g. `SystemCommands/RegisterSystemCommands.cs`).
- **Command names must be lowercase** — lookup lowercases input, and tests assert `Name == Name.ToLower()`.
- **Every command must provide help text and a category** — implement `Description` (one-line summary shown by `!help`), `Usage` (argument summary shown by `!help <name>`), and `Category` (the help-list group, from the `CommandCategory` enum) on the command implementation. Registration rejects commands without them, and the registration-contract test enforces it for every registered command — do not re-assert lowercase names, help text, categories, or tier validity per command in tests; assert only what is specific to that command (its expected name, its default tier, its pipeline resolution). See `docs/software-design.md` §3.2 for the help-command design.

### Permissions

Tiered integer permission system (`Commands/PermissionsService.cs`): a user may run a command when their `User.PermissionTier` >= the command's `CommandMetaData.PermissionTier`. Missing DB entries default to tier 0. Tiers are managed at runtime via the `!commandpermission` and `!userpermission` commands.

### Data model

`Connections/DatabaseContext.cs` defines eight DbSets with nested entity classes — six scoped per guild, one per user, one global:

- `User` (composite key `GuildId` + `DiscordId`, `PermissionTier`)
- `CommandMetaData` (composite key `GuildId` + `CommandName`, `PermissionTier`)
- `GuildSetting` (key `GuildId`; `CommandIdentifier` override, per-tier cooldown overrides used by the cooldown service, `OperatorAgentTier` — the per-guild tier the MCP operator agent acts at, default the lowest tier, managed by `!agenttier`, design §3.11)
- `Quote` (composite key `GuildId` + `Number`; `Text`, `AddedByDiscordId`, `CreatedAt` — per-guild sequential numbering, managed by the `Commands/QuoteCommands/` commands)
- `CustomResponse` (composite key `GuildId` + `Name`; `Content`, `LastModifiedByDiscordId`, timestamps — managed by the `Commands/ResponseCommands/` commands and dispatched by `CommandHandler` on unknown tokens)
- `Reminder` (composite key `OwnerDiscordId` + `Number`; `Message`, `DueAtUtc`, nullable `RecurrenceInterval` (null = one-shot; the `!remindme every <duration>` recurring form, re-scheduled from the delivery time), nullable `GuildId`, `CreatedAt`, `FailedAtUtc` — per-user reminders managed by the `Commands/ReminderCommands/` commands and delivered as DMs by the `Scheduling/ScheduledMessageDeliveryService` engine fed by `Reminders/ReminderMessageSource`)
- `BotSetting` (key `Key`; `Value` — global key-value settings, first key: the operator notification channel id managed by `!notifychannel`)
- `BotBan` (composite key `GuildId` + `DiscordId`; `BannedByDiscordId`, `CreatedAt`, nullable `ExpiresAtUtc` — null = permanent — the per-guild user bot bans managed by the `Commands/SystemCommands/` ban commands and enforced by the pipeline via `Commands/BotBans.cs`, with lazy on-sight expiry cleanup)

EF Core migrations live in `HambotV2/Migrations/`. Regenerate with `dotnet ef migrations add <Name>` from the `HambotV2/` project directory (a `DesignTimeDatabaseContextFactory` lets the tools scaffold without running startup; connection string comes from `appsecrets.json`, see below).

## Configuration & Secrets

- `appsettings.json` holds non-secret bot config (`BotConfiguration:BotName`, `LogFormat`, `CommandIdentifier`).
- **`HambotV2/appsecrets.json` is gitignored** and holds the Discord bot token (`Secrets:BotToken`) and the PostgreSQL connection string (`ConnectionStrings:DefaultConnection`), plus the optional secret MCP section (`Mcp:Enabled` / `Mcp:ApiKey` / `Mcp:ListenUri` — see `Mcp/`). Its expected structure is documented in `README.md`. Never commit secrets; never reference real tokens in code or tests.

## Git

- **Do not stage or commit anything to git yourself.** Never run `git add` or `git commit`; leave all changes unstaged in the working tree and let the user handle version control.

## Conventions

- C# with nullable reference types and implicit usings enabled; `LangVersion` is `latest` in the test project.
- Most bot-internal types are `internal` (tests rely on `InternalsVisibleTo`); keep new command implementations `internal` too.
- DI lifetimes: `CommandService` is a singleton; `CommandHandler`, `PermissionsService`, and `DatabaseContext` are transient. The handler is resolved per message and disposed after use.
- Container deployment via `HambotV2/Dockerfile` (targets the `HambotV2` project) or `docker-compose.yml` (bot + PostgreSQL + backup sidecar; connection string defaults to the compose `db` service, plain containers use `host.docker.internal:5432`).
- Docs: `docs/software-design.md` is the system design (marked **(planned)** sections describe future roadmap work — everything else is as-built), `docs/software-requirements.md` holds the numbered requirements (FR/NFR), `docs/roadmap.md` tracks the backlog and contains only outstanding work — never add completed features, status lists, or historical entries to it; when a task is signed off, remove it from the roadmap, fold the as-built behavior into the design and requirements docs instead, and resolve any references to it elsewhere in the project, `docs/manual-test-plan.md` is the pre-production test checklist, and `docs/jenkins-cicd.md` covers the deploy pipeline.

## Code style

The codebase is deliberately clean, minimalistic, and readable **without** needing comments. For new code:

- **Let the code speak**: prefer descriptive type/method/variable names and small, single-purpose methods over comments that narrate what the next lines do. Do not write comments that restate a method name, a test name, or the code itself.
- **Comments only for the non-obvious**: a short comment is fine when it records a constraint, a race, a workaround, or a *why* that the code cannot express (see the wake-up-signal snapshot in `Scheduling/ScheduledMessageDeliveryService.cs` or the sentinel-guild-id note in `CooldownService` for the tone and length). Anything longer belongs in `docs/software-design.md`.
- **No spec references in code**: do not sprinkle `FR-xx`/`NFR-xx` tags or `design §x`/`(design §x.y)` references through code, tests, comments, or doc comments — traceability lives in `docs/software-requirements.md` and `docs/software-design.md`, not in the source. An occasional short reference is acceptable only where a feature is complex enough that the design doc is the only place recording the full behavior or rationale (e.g. the multi-component presence service or the delivery engine's wake-up semantics); routine code, commands, and tests get none.
- **Keep XML docs short**: one- or two-line `<summary>` blocks stating the member's role and its non-obvious contract. No multi-paragraph essays; no `---` section-divider comments.
- **Merge over fragment**: do not split code into one-line helper methods that are used once and add indirection without clarity. Conversely, keep genuinely shared logic in one place (e.g. `CommandArguments`, `ProfileImageCommandBase`).
- **Tests assert behavior, once**: name tests for the observable behavior they verify; each behavior gets exactly one home — duplicate assertions of a global contract (command registration, help text) already live in `TestCommandInfrastructure.CommandService_All_Registered_Commands_Satisfy_Contract`. When waiting on asynchronous state in tests, wait for the *persisted* end state, not an intermediate signal (see `TestReminders.WaitUntilAsync` usage).
- **Cleanup passes**: when tidying or finishing a file, remove comments that violate the rules above — spec/`§` references on routine code, narrating comments, stale references to renamed or removed behavior — in the same pass, so the touched file ends up consistent with this style instead of carrying the debt forward.
