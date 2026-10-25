# Software Design

This document describes the current design of HambotV2 as implemented today. It is a snapshot of the existing system, intended as an overview for contributors and as a baseline for future changes. Section 9 is the exception: it records the agreed designs for the not-yet-implemented Phase 3 roadmap tasks (T13, T14).

## 1. System Overview

HambotV2 is a Discord chat bot built on .NET 10 using the generic host (`Microsoft.Extensions.Hosting`). It connects to the Discord gateway via **Discord.Net 3.20.2**, persists state in **PostgreSQL** through **EF Core 10 / Npgsql**, and processes chat commands through a small, custom command pipeline with a tiered permission system.

The solution contains two projects:

| Project | Purpose |
|---|---|
| `HambotV2/` | The bot itself (Worker SDK, generic host). Hosted service, command pipeline, EF Core model and migrations. |
| `BotTests/` | MSTest + Moq unit tests. Uses `InternalsVisibleTo` into the main project; runs without a database or Discord token. |

Key external dependencies:

- `Discord.Net` 3.20.2 — gateway connection, message events
- `Microsoft.EntityFrameworkCore` 10 / `Npgsql.EntityFrameworkCore.PostgreSQL` — persistence
- `Microsoft.Extensions.Hosting` — DI, logging, configuration, hosted services
- `Serilog` (+ `Serilog.Extensions.Hosting`, `Serilog.Settings.Configuration`, console/file sinks) — logging backend (roadmap T12)
- `MSTest` + `Moq` (test project only)

## 2. Runtime Architecture

```
                    ┌──────────────────────────────────────────────┐
                    │  Generic Host (Program.Main)                 │
                    │  - Configuration (appsettings + appsecrets)  │
                    │  - DI container                              │
                    │  - Logging                                   │
                    └───────────────┬──────────────────────────────┘
                                    │ hosts
                                    ▼
┌────────────────┐  message   ┌──────────────────┐
│ Discord gateway│ ──────────► │ DiscordClient    │ (BackgroundService)
│ (Discord.Net)  │             │ Connections/     │
└────────────────┘             └────────┬─────────┘
                                        │ messages starting with "!"
                                        ▼
                               ┌──────────────────┐        ┌──────────────────┐
                               │ CommandHandler   │ ──────►│ PermissionsService│
                               │ (per message)    │        └────────┬─────────┘
                               └──────┬───────────┘                 │
                                      │ resolve + execute           ▼
                              ┌───────▼───────────┐        ┌──────────────────┐
                              │ CommandService    │        │ DatabaseContext  │
                              │ (singleton, lazy   │        │ (EF Core/Npgsql, │
                              │  command registry)│        │  transient)      │
                              └───────────────────┘        └──────────────────┘
```

### 2.1 Composition root — `HambotV2/Program.cs`

- Registers `appsecrets.json` as an additional configuration source.
- Replaces the default logging providers with Serilog (`builder.Logging.ClearProviders()` + `AddSerilog`), configured via `ReadFrom.Configuration` from the `Serilog` configuration section, so sinks and levels come from `appsettings.json` / environment variables (see §4).
- DI registrations:

| Service | Interface | Lifetime |
|---|---|---|
| `DiscordClient` | hosted service | singleton (hosted) |
| `CommandService` | `ICommandService` | singleton |
| `CommandHandler` | `ICommandHandler` | transient (resolved per message, disposed after use) |
| `PermissionsService` | `IPermissionsService` | transient |
| `DatabaseContext` | concrete `DbContext` and `IDatabaseContext` | transient (both service and options lifetime) |

## 3. Component Design

### 3.1 Discord connection — `Connections/DiscordClient.cs`

A `BackgroundService` that:

1. Creates a `DiscordSocketClient` with only the gateway intents the bot uses: `Guilds | GuildMessages | DirectMessages | MessageContent` (explicit intents instead of `AllUnprivileged` avoid Discord.Net warnings for unused intents).
2. Logs in with the token from configuration key `Secrets:BotToken` and starts the gateway.
3. Subscribes to `MessageReceived`. Messages from bots and webhooks (including the bot itself) are ignored — otherwise the bot would re-process its own replies that start with the identifier (e.g. the `!help` detail output). Any remaining message whose content starts with the configured `BotConfiguration:CommandIdentifier` (default `!`) is forwarded to a freshly resolved `ICommandHandler` (resolved from `IServiceProvider`, disposed after handling).
4. Bridges `Discord.Log` events to `Microsoft.Extensions.Logging` using the configurable `BotConfiguration:LogFormat` format string. Gateway connection events are logged explicitly: `Disconnected` at Warning (including the reason), `Connected` at Information (fires both for the initial connection and for Discord.Net's automatic reconnects).
5. Runs a keep-alive loop that logs gateway connection state every 5 minutes.
6. On host shutdown (or any exit path, including startup failures) stops the gateway, logs out, and disposes the `DiscordSocketClient`, logging the shutdown ("Shutting down Discord connection..." → "Discord connection shut down cleanly.") instead of dying mid-connection.

### 3.2 Command pipeline — `Commands/`

**`ICommand`** (`Commands/ICommand.cs`) — the command contract:

- `string Name` — must be lowercase (lookup lowercases input; registration rejects anything else).
- `string Description` — one-line summary shown in the `!help` list.
- `string Usage` — argument summary without the command identifier, e.g. `userpermission <user> [tier]`.
- `int DefaultPermissionTier` — required tier when no `CommandMetaData` override exists in the database (see 3.3).
- `Task ExecuteCommand(IMessage, ICommandContext)` — the command body. The pipeline operates on the `IMessage` interface (which `SocketMessage` implements) so the handler is unit-testable with mocks.

Help text lives on the command implementation itself, so it cannot drift from the code or the registration chain, and no parallel registry has to be kept in sync.

**`ICommandContext` / `CommandContext`** — a thin wrapper exposing the per-message `IDatabaseContext` to command implementations, keeping commands decoupled from DI. It also carries a `TierOverrides` record (see 3.3) — the message's guild owner plus the configured bot owner id — and `GetUserEffectiveTier(userId)` computes a user's effective tier so commands share the exact semantics the permission check uses.

**`CommandHandler`** (`ICommandHandler`) — per-message orchestrator, transient and disposable:

1. Resolve the command via `ICommandService.TryGetCommand`; identifier-only messages (a bare `!`) are ignored silently, unknown commands get "The requested command is unavailable".
2. Check permissions via `IPermissionsService.CheckUserPermissions` (passing a `TierOverrides` built from the message's guild owner and the configured bot owner id); if insufficient, reply "You are not permitted to use that command".
3. Execute the command with a `CommandContext` wrapping the per-message `IDatabaseContext` and the permission context.

The whole pipeline is wrapped in a try/catch: on any failure (throwing command, database outage during the permission check, ...) the exception is logged with the command name and message content, and the user gets a generic in-channel error message ("An unexpected error occurred...") that never contains exception details. Each failing message is handled independently — a single failure never takes the bot down, and subsequent messages keep being processed. On disposal the handler also disposes its per-message `DatabaseContext`, so connections are released after every message regardless of success or failure.

**Command audit event** (roadmap T12, NFR-11) — `HandleCommand` emits exactly one structured Information-level event per handled message, in a `finally` block so every exit path is covered (including throws). The event carries named fields for filterability: `CommandName` (resolved name, null when unknown/identifier-only), `Outcome` (the `CommandOutcome` enum in `Commands/CommandOutcome.cs`: `Executed`, `DeniedPermission`, `UnknownCommand`, `Ignored` for bare-identifier messages, `Failed`; `Throttled` and `DmRejected` are reserved for roadmap T13/T14), `GuildId` (null in DMs), `ChannelId`, `UserId`, and `DurationMs` (handler wall time). Message content never appears in the audit event — only the Error-level failure log contains it — keeping routine usage logs free of user text. This single event replaces the former per-branch log lines, making command usage traceable from logs without debugging.

**`CommandService`** (`ICommandService`) — singleton command registry:

- Holds a `Dictionary<string, ICommand>` keyed by command name.
- Lazily populated on first lookup (`CommandsRegistered` flag) by calling `RegisterAllCommands.RegisterCommands`.
- `RegisterCommand` is the single chokepoint for the command contract: it rejects non-lowercase or empty names, missing `Description`/`Usage`, and invalid `DefaultPermissionTier` values, so a command without help text can never enter the registry.
- `TryGetCommand` parses via the shared `CommandParsing` helper. Parsing rules: the command identifier (`BotConfiguration:CommandIdentifier`, default `!`) is matched case-sensitively as an exact prefix of the trimmed message; any run of whitespace separates tokens; the command name is the first token after the identifier, lowercased. Bare identifier-only messages (e.g. `!` or `!   `) resolve to no command.
- `IsIdentifierOnlyMessage` lets the handler distinguish "no command name given" (ignored silently) from "unknown command" (answered).

**Command registration** — a small static registration chain:

- `Commands/RegisterAllCommands.cs` → delegates to `Commands/SystemCommands/RegisterSystemCommands.cs`.
- To add a command: implement `ICommand` (system commands live in `Commands/SystemCommands/`) and register it in the relevant `Register*Commands` class.

**Shared argument parsing — `Commands/CommandArguments.cs`** (roadmap T15, NFR-13) — commands receive typed arguments instead of hand-rolled `Split`/`TryParse` code. `CommandArguments.Parse(content)` splits the raw message content on runs of spaces and drops the first token (identifier + command name), so index 0 is the command's first argument. Accessors: `Count`, `Get(index)` (raw token or null — commands echo it back in replies), and the typed parsers `TryGetString`, `TryGetInt`, and `TryGetDiscordUserId` (raw id or `<@id>`/`<@!id>` mention, delegating to `DiscordUserId`). Quoting is not supported; one whitespace-separated token is one argument. All existing commands (`HelpCommand`, `CommandPermission`, `UserPermission`) are built on it; new commands (e.g. T14's `!settings`) must use it too instead of hand-parsing.

### 3.3 Permissions — `Commands/PermissionsService.cs`

Tiered integer permission model with a named scale (roadmap T16, FR-33):

| Tier | Name | Meaning |
|---|---|---|
| 0 | everyone | Default for users and most commands. |
| 1 | moderator | Trusted helpers. |
| 2 | admin | Can manage permissions (below their own tier). |
| 3 | owner | Full control of a guild; held automatically by the guild owner. |
| 4 | bot owner | Highest tier of all, above every guild. Configuration only — never assignable via commands. |

The scale lives in `Commands/PermissionTier.cs` (constants, `IsValid`, `GetName`); inputs outside 0–4 are rejected with an in-channel error (FR-37).

- A command may run when the user's **effective tier** ≥ the command's **required tier**.
- A command's required tier is its database `CommandMetaData` override if present, otherwise the command's built-in `DefaultPermissionTier` (0 for most commands, **admin** for `!commandpermission`/`!userpermission`, FR-35).
- A user's effective tier is the stored database tier (0 if no row), raised to **owner** for the guild owner of the message's guild (FR-34). This gives a fresh guild a working first admin without any database setup.
- The **bot owner** configured under `Permissions:BotOwnerDiscordId` (in containers supplied via `BOT_OWNER_DISCORD_ID` in `.env` through compose) acts at tier 4 in every guild — the highest permission level of all, above guild owners. It is the designated tier for things that apply across guilds (currently the global command permission tiers; later per-guild settings fallbacks, roadmap T14). The tier is configuration-only: the escalation guard math (strictly-below-own-tier) makes it unassignable through commands.
- `IPermissionsService` exposes `CheckUserPermissions` plus the read accessors `GetUserEffectiveTier` and `GetCommandRequiredTier`, so the default semantics exist in exactly one place and `HelpCommand` reuses them. The implicit-tier ids travel as a single `TierOverrides` record (`Commands/TierOverrides.cs`), which also owns the effective-tier computation (`EffectiveTierOf`) — no method needs a parameter per override source.

**Escalation guard** (FR-36), enforced inside the permission-management commands:

- `!userpermission <user> <tier>`: the invoker can never modify their own tier, can only modify users whose effective tier is strictly below their own, and can only assign tiers strictly below their own. Target arguments accept raw ids and Discord mentions (`<@id>`, `<@!id>`).
- `!commandpermission <name> <tier>`: the invoker can only assign required tiers strictly below their own, and only to commands whose current required tier is strictly below their own — nobody can move a command gate to or past their own level.

Tiers are read from the database per message and managed at runtime via the two commands, so no code change or restart is needed to adjust access.

### 3.4 Persistence — `Connections/DatabaseContext.cs`

EF Core `DbContext` against PostgreSQL with two nested entity classes:

| Entity | Key | Fields |
|---|---|---|
| `User` | `DiscordId` (ulong) | `PermissionTier` (int) |
| `CommandMetaData` | `CommandName` (string) | `PermissionTier` (int) |

EF Core migrations live in `HambotV2/Migrations/` (initial create only). Regenerate with `dotnet ef migrations add <Name>` from the `HambotV2/` project directory.

Migrations are applied automatically on startup: `Program.Main` runs `Database.Migrate()` before the Discord gateway starts. If the database is unreachable, a critical error is logged (naming the connection configuration) and the host exits with a non-zero code instead of starting a half-functional bot. No manual migration step is required.

There is also an `Connections/IDatabaseContext.cs` abstraction exposing the two `DbSet`s and `SaveChangesAsync`. The whole command pipeline (`CommandHandler`, `CommandContext`, `PermissionsService`) depends on `IDatabaseContext`, and `Program.cs` maps it onto the registered transient `DatabaseContext`. This is what lets the command tests run against the EF InMemory provider instead of PostgreSQL.

### 3.5 Commands currently implemented

All in `Commands/SystemCommands/`, registered by `RegisterSystemCommands`:

| Command | Name | Behavior |
|---|---|---|
| `HelloWorld` | `!hello` | Replies "Hello World". |
| `HelpCommand` | `!help [commandName]` | Lists the commands available to the invoking user (required tier ≤ their effective tier), one line per command: identifier + name — description. With an argument: detailed help (description, usage, required tier) for that command — only if the user can run it; unknown and insufficient-tier names get the same generic "The requested command is unavailable" reply, so the existence of higher-tier commands is never revealed (FR-40). |
| `CommandPermission` | `!commandpermission <name> [tier]` | With 1 arg: prints the command's required tier (database override, or a hint that the built-in default applies). With 2 args: sets/creates the command's tier, guarded by the escalation rules in 3.3. Admin-only by default. |
| `UserPermission` | `!userpermission <user> [tier]` | With 1 arg: prints the user's effective tier (with a "guild owner" / "bot owner" note where applicable). With 2 args: sets/creates the user's tier, guarded by the escalation rules in 3.3. `<user>` accepts raw ids and `@mentions`. Admin-only by default. |

`PermissionPersistence` holds the shared "upsert tier" logic of the two permission commands.

## 4. Configuration

- `appsettings.json` (committed, non-secret): `BotConfiguration:BotName`, `BotConfiguration:LogFormat`, `BotConfiguration:CommandIdentifier` (default `!`), plus the `Serilog` section.
- **Logging (roadmap T12)**: Serilog replaces the host's default logging providers (registered in `Program.cs` via `ClearProviders()` + `AddSerilog`, configured with `ReadFrom.Configuration`). The `Serilog` section defines the minimum level (with per-namespace overrides, e.g. `Microsoft`/`System` at Warning) and the sinks: Console and a daily-rolling File sink to `logs/hambot-.log` with 31 days retention (the `logs/` output is gitignored; in containers it lives in the container filesystem — mount a volume if retention matters). The level is adjustable at deploy time via `appsettings.json` or environment variables without a rebuild; `appsettings.Development.json` raises the default to Debug for local development. `BotConfiguration:LogFormat` is unrelated to the sinks: it remains the format string for bridging Discord.Net's `Discord.Log` events (§3.1). All bot code keeps using `ILogger<T>`; Serilog is an implementation detail of the composition root.
- `HambotV2/appsecrets.json` (gitignored, optional): `Secrets:BotToken` (Discord bot token) and `ConnectionStrings:DefaultConnection` (PostgreSQL connection string). Loaded explicitly in `Program.cs` for local development. Its expected structure is documented in `README.md`. Secrets must never be committed.
- Environment variables (e.g. `Secrets__BotToken`, `ConnectionStrings__DefaultConnection`) are the primary source in containers; `Program.cs` re-adds the environment provider after the secrets file so env vars take precedence. `appsecrets.json` is excluded from the Docker build context (`.dockerignore`), so the published image is secret-free.
- Optional key: `Permissions:BotOwnerDiscordId` (`BOT_OWNER_DISCORD_ID` in `.env`, wired through docker-compose) — the bot owner, holding the globally highest tier (4). Unset by default; guild owners always act at owner tier without any configuration.
- Required keys (`Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, `BotConfiguration:BotName`, `BotConfiguration:LogFormat`, `BotConfiguration:CommandIdentifier`) are validated at startup (`ConfigurationValidator`): any missing key is logged by name and the host exits with a non-zero code before any connection attempt.

## 5. Deployment

The bot is containerized via `HambotV2/Dockerfile` (multi-stage .NET 10 build → `dotnet publish` → runtime-only final image). The build context is the repository root, and the image is secret-free: `appsecrets.json` is excluded via `.dockerignore`, secrets are supplied as environment variables (`Secrets__BotToken`, `ConnectionStrings__DefaultConnection`).

The recommended deployment is `docker-compose.yml` at the repository root, which brings up PostgreSQL (`postgres:17` with a healthcheck and persistent `db-data` volume) and the bot (waiting for the database to become healthy). Compose variables come from a gitignored `.env` file (structure in `.env.example`):

```bash
cp .env.example .env   # fill in DISCORD_BOT_TOKEN and POSTGRES_PASSWORD
docker compose up -d --build
```

Plain Docker is also possible:

```bash
docker build -f HambotV2/Dockerfile -t hambotv2:latest .
docker run -d --name hambot-smoke -e Secrets__BotToken=... -e "ConnectionStrings__DefaultConnection=..." hambotv2:latest
```

- Startup order: configuration validation → database migration → Discord gateway login. A missing required key or an unreachable database fails fast with a critical log line and a non-zero exit code.
- `docker-compose.yml` defaults the connection string to the compose `db` service (`host.docker.internal` is the equivalent for a plain container against Postgres on the Docker host).

## 6. Testing

- `BotTests/` contains MSTest + Moq tests: `TestCommandInfrastructure.cs`, `TestCommands.cs`, `TestPermissions.cs`, `TestCommandHandler.cs`, `TestConfigurationValidator.cs`, `TestPermissionCommands.cs`, `TestHelp.cs`, `TestCommandArguments.cs`, plus the shared `DiscordTestDoubles.cs`.
- Tests run without a reachable database or Discord token: Moq doubles the Discord message surface, and command/permission tests run against the EF Core **InMemory** provider through the `IDatabaseContext` abstraction (isolated database per test).
- Coverage focuses on: command registration contract (lowercase names, help text, valid default tiers), robust command parsing (bare identifier, whitespace variations, custom identifiers), command lookup, the argument-parsing helper (tokenization, typed accessors, mention parsing), the permission tier scale with tier overrides and escalation guards, permission-management command behavior (mentions, tier validation, upserts, guard rejections), `!help` listing/detail/non-revealing denials, the `CommandHandler` failure paths (throwing command, throwing permission check, denied permissions, unknown command, bare-identifier ignore, `DatabaseContext` disposal), the structured command audit event (exactly one Information event per message with the expected `CommandOutcome`, no message content in the audit line), and startup configuration validation.
- Build and run:

  ```bash
  dotnet build HambotV2.sln
  dotnet test HambotV2.sln
  ```

- The build is warning-free (roadmap T11) and CI treats warnings as errors; new code must not add any.

## 7. Conventions

- C# with nullable reference types and implicit usings enabled; `LangVersion` latest in the test project.
- Bot-internal types are `internal` (tests rely on `InternalsVisibleTo`, which also grants `DynamicProxyGenAssembly2` for Moq); new command implementations should be `internal` too.
- Command names must be lowercase.
- DI lifetimes: `CommandService` singleton; `CommandHandler`, `PermissionsService`, `DatabaseContext` transient; handler resolved per message and disposed after use.

## 8. Known Design Considerations / Current Limitations

These are observations of the current implementation, not roadmap commitments:

- **State is global**: permission tiers and settings are shared across all guilds; per-guild scoping is roadmap task T14 (agreed design in §9.2).
- **CI**: a declarative `Jenkinsfile` at the repository root builds the solution and runs the tests (with nullable warnings treated as errors); the Jenkins job must be configured as "Pipeline script from SCM" pointing at it.

## 9. Planned Designs — Phase 3 (T13, T14)

This section records the **agreed designs for the not-yet-implemented Phase 3 roadmap tasks** (T13 command cooldowns, T14 per-guild scoping). Roadmap T14 explicitly requires the design to be documented before implementation starts; the same convention is applied to T13 here. When a task is implemented, fold its subsection into the component/ configuration sections above (§2–§7) and remove it from here. Both designs build on the implemented T15 argument-parsing framework (§3.2): T14's `!settings` command and T13 stage 2's `!settings cooldown` subcommand are written on `CommandArguments`. T13's *per-guild runtime* configuration depends on T14's `GuildSetting` storage and is staged accordingly (§9.1). T12 (structured logging) has been implemented and folded into §3.2/§4.

### 9.1 T13 — Command cooldowns / rate limiting (NFR-12)

**Decision — per-(guild, user, command) in-memory cooldown; the cooldown window is configured per permission tier, per guild, at runtime.**

- **Service**: `Commands/ICooldownService.cs` + `CooldownService`, registered as a **singleton** (state is shared across messages; the rest of the handler chain stays transient, NFR-10). Internally a `ConcurrentDictionary<CooldownKey, DateTimeOffset>` where `CooldownKey = (ulong GuildId, ulong UserId, string CommandName)`.
- **Time source**: an injected `TimeProvider` (.NET built-in abstraction) instead of `DateTimeOffset.UtcNow`, so unit tests advance time deterministically (`Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider` in the test project).
- **Window resolution (per tier, per guild)** — the window applied to an invocation is resolved from the invoking user's **effective tier** in that guild (§3.3), looked up in this order:

  1. The guild's per-tier override stored in its `GuildSetting` row (§9.2), if present for that tier.
  2. Otherwise the global per-tier default from `BotConfiguration:CooldownSecondsByTier` in `appsettings.json` (container override via `BotConfiguration__CooldownSecondsByTier__0` etc.).

  A value of `0` (or negative) disables the cooldown for that tier in that scope. **Tier 4 (bot owner) is excluded from per-guild configuration** — it is a global, configuration-only role (FR-41), so a single guild's settings must not bind it; tier 4 always uses the global default. Configurable tiers are therefore 0–3, validated against the `PermissionTier` scale (FR-37 style).

- **Pipeline placement**: in `CommandHandler`, **after** the permission check and **before** `ExecuteCommand`. The permission check already computes the user's effective tier (§3.3), so the cooldown check consumes that result with no extra database lookup. Trade-off accepted: denied invocations are not throttled (they are answered by the same cheap denial path as before) — but the window cannot be bypassed via permission lookups either, because the tier used for resolution is the guarded one. The throttle key is per command, so a user hitting `!hello` is not blocked from `!help`.
- **Behavior on throttle**: reply in-channel with a user-friendly message naming the remaining wait, e.g. *"You're using commands too quickly — please wait 2 more seconds."* The invocation is logged with `Outcome=Throttled` (§3.2) and the command does not execute.
- **Runtime configuration** — managed per guild, no restart or code change:
  - **Global defaults** live in `appsettings.json` as a map, e.g. `"CooldownSecondsByTier": { "0": 3, "1": 3, "2": 2, "3": 1 }` (default 3 seconds for every tier when the map or a tier entry is absent).
  - **Per-guild overrides** are persisted in the `GuildSetting` entity (§9.2) as one nullable int column per configurable tier (`CooldownSecondsTier0`–`CooldownSecondsTier3`; `null` = inherit the global default for that tier).
  - **Management command**: `!settings cooldown <tier> <seconds>` sets and `!settings cooldown <tier> reset` clears the invoking guild's override (part of the `!settings` command defined in §9.2; tier input validated against the scale, seconds ≥ 0). The **escalation guard applies, mirroring FR-36**: an invoker may only configure cooldowns for tiers strictly below their own effective tier — nobody can loosen the throttle that governs their own level — while the bot owner (tier 4) may configure any tier's cooldown in any guild.
- **Staging (roadmap order)**: T13 ships before T14, so the rollout is two steps:
  1. **With T13**: the in-memory throttle plus the window resolution above, with the global `CooldownSecondsByTier` defaults only (config-file sourced).
  2. **With T14**: the per-guild runtime override — the `GuildSetting` columns and the `!settings cooldown` subcommand — plugged into the same resolution chain as its highest-priority source. The tier-resolution logic from step 1 is reused unchanged.
- **Memory bound**: expired entries are removed opportunistically when their key is next checked, so the dictionary stays bounded by the number of distinct (guild, user, command) triples recently seen.
- **Tests**: second invocation inside the window is throttled and answered with the remaining-wait message; invocation after `FakeTimeProvider.Advance(window)` succeeds; windows are independent per user, per command, and per guild; different tiers get their configured windows (global map, and — once T14 lands — guild override beats global default, `reset` falls back); `0` disables throttling for that tier; the escalation guard rejects configuring one's own or higher tiers; tier 4 is never bound by a guild override.

### 9.2 T14 — Per-guild configuration and permission scoping (FR-24, FR-25, FR-26)

**Goal**: all persisted state scoped per guild; guild-scoped settings with global defaults; a defined DM fallback. This is the largest Phase 3 change — schema migration plus pipeline changes — and builds on the T16 tier model (§3.3).

#### Data model & migration

`Connections/DatabaseContext.cs` gains a `GuildId` dimension everywhere:

| Entity | Key (was) | Key (becomes) |
|---|---|---|
| `User` | `DiscordId` | `(GuildId ulong, DiscordId ulong)` |
| `CommandMetaData` | `CommandName` | `(GuildId ulong, CommandName string)` |
| `GuildSetting` (new) | — | `GuildId ulong`, with `CommandIdentifier` (string, null until overridden) and the per-tier cooldown overrides `CooldownSecondsTier0`–`CooldownSecondsTier3` (nullable int, null = inherit the global default; see §9.1) |

`IDatabaseContext` exposes the new `GuildSettings` DbSet alongside the existing two. One new migration (`dotnet ef migrations add PerGuildScoping` from the `HambotV2/` project) adds the columns, switches the primary keys to composites, and creates the `GuildSetting` table. Pre-existing rows cannot be attributed to a guild, so the migration keeps them under the sentinel `GuildId = 0`, where they match no real guild; they are inert and can be cleaned up manually. Fresh guilds need no data anyway: the guild owner already bootstraps at owner tier (FR-34).

#### Permission scoping (FR-24)

- `IPermissionsService` lookups take a `guildId` parameter: user tiers are read by `(guildId, userId)`, command tiers by `(guildId, commandName)`. The tier scale and the `TierOverrides` record are unchanged — the guild-owner override is already per-guild by construction (it is derived from the message's guild).
- The permission-management commands act on the **invoking guild only**; `!userpermission`/`!commandpermission` report and set tiers in the guild the command was run in. The escalation guard (§3.3) then naturally compares tiers within that guild.
- The **bot owner** (tier 4, FR-41) remains globally effective in every guild — the designated authority for anything cross-guild, per the committed design.
- `!help` filtering (§3.5) needs no change beyond passing the guild through: it already reuses the permission service's semantics.

#### DM fallback (FR-25) — settled: reject

Command invocations received in direct messages are **not executed**: the bot replies with a clear in-channel message ("Commands are only available in servers — run them in a guild I am a member of.") and logs the invocation with `Outcome=DmRejected` (§3.2). The `DirectMessages` gateway intent stays enabled precisely so this rejection can be delivered. Consequences: there is no global/default permission scope to reason about, permission management cannot leak across guilds via DMs, and `GuildId` is never null in a permission lookup.

#### Guild-scoped settings (FR-26)

- New `IGuildSettingsService` (singleton, `Commands/GuildSettingsService.cs`): `GetCommandIdentifier(guildId)` returns the guild's `GuildSetting` override if present, otherwise the global default from `BotConfiguration:CommandIdentifier`. Results are cached per guild and invalidated when a setting is written, so the per-message path stays a memory lookup.
- **Detection flow change**: today `DiscordClient` pre-filters on the global identifier straight from configuration (§3.1). With per-guild identifiers the pre-filter instead asks `IGuildSettingsService` for the *effective identifier of the message's guild* (DMs use the global default) and forwards matching messages; `CommandService.TryGetCommand` / `CommandParsing` receive that same identifier as a parameter instead of reading configuration. One source of truth for the effective identifier, same parsing rules as §3.2.
- **Management command**: a `!settings` command (system command, default tier **admin**, mirroring FR-35's rationale) manages the invoking guild's overrides: `!settings` lists current values and the global defaults; `!settings commandidentifier <value>` sets the guild's identifier override (validated non-empty, ≤ a few characters); `!settings commandidentifier reset` clears it; `!settings cooldown <tier> <seconds|reset>` manages the per-tier cooldown overrides exactly as designed in §9.1 (escalation guard: tiers strictly below the invoker's own only). The bot owner (tier 4) can manage any guild's settings by running the command there. Help text follows the FR-39/registration contract like any other command.

#### Tests

- A user's tier and a command's required tier in one guild have no effect in another guild (same user/command, two guild ids, different rows).
- Guild identifier override changes detection/parsing; unset guilds fall back to the global default.
- DM invocations are rejected with the defined message.
- `!settings` set/reset/permission behavior for both managed settings (identifier, per-tier cooldowns — including the §9.1 escalation guard and tier-4 exclusion).
- Migration: existing command/permission tests keep running against the EF InMemory provider via `IDatabaseContext`; the startup auto-migration path (§3.4) is unchanged.
