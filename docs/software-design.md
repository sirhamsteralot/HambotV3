# Software Design

This document describes the design of HambotV2. It is the overview for contributors and the baseline for changes. Unless a section is marked **(planned)**, it describes the system as implemented today.

Three features from [`software-requirements.md`](software-requirements.md) were specified ahead of implementation: **quotes** (FR-42–FR-50, plus the legacy import FR-72–FR-75, NFR-14), **custom command responses** (FR-51–FR-60, NFR-15), and **reminders** (FR-61–FR-71, NFR-16), designed as §3.6, §3.7, and §3.8. All three are implemented, along with the Jenkins auto-deploy pipeline (§5.1) and **version reporting** (FR-76–FR-77, NFR-17, §3.9). (§3.6–§3.9 describe the system as it is.) When future work lands, fold any deviations back here so this document keeps describing the system as it actually is.

For the post-1.0.0 roadmap, two more features are specified ahead of implementation; their sections below are marked **(planned)**: the **MCP server** (FR-86–FR-91 / NFR-20, §3.11) and **LLM-driven presence and bio updates** (FR-92–FR-95, FR-99, FR-114 / NFR-21, §3.12). Those sections describe the design as intended, not the implemented system; when each lands, remove its **(planned)** marker and fold the as-built behavior back in. The already-implemented post-1.0.0 features — **database backups** (FR-81–FR-85, FR-96–FR-98, FR-100–FR-101 / NFR-19, NFR-22, §5.2), the **watchdog, health reporting and operator notifications** (FR-105–FR-106, FR-117–FR-118 / NFR-23, §3.13), **Discord presence & profile identity** (FR-78–FR-80, FR-113, FR-115–FR-116 / NFR-18, §3.10), **recurring reminders** (FR-107–FR-109 / NFR-24, §3.14), and **Discord application commands** (FR-110–FR-112 / NFR-26, §3.15) — are, like everything else not marked **(planned)**, described as built.

## 1. System Overview

HambotV2 is a Discord chat bot built on .NET 10 using the generic host (`Microsoft.Extensions.Hosting`). It connects to the Discord gateway via **Discord.Net 3.20.2**, persists state in **PostgreSQL** through **EF Core 10 / Npgsql**, and processes chat commands through a small, custom command pipeline with a tiered permission system scoped per Discord guild (FR-24).

The solution contains two projects:

| Project | Purpose |
|---|---|
| `HambotV2/` | The bot itself (Worker SDK, generic host). Hosted service, command pipeline, EF Core model and migrations. |
| `BotTests/` | MSTest + Moq unit tests. Uses `InternalsVisibleTo` into the main project; runs without a database or Discord token. |

Key external dependencies:

- `Discord.Net` 3.20.2 — gateway connection, message events
- `Microsoft.EntityFrameworkCore` 10 / `Npgsql.EntityFrameworkCore.PostgreSQL` — persistence
- `Microsoft.Extensions.Hosting` — DI, logging, configuration, hosted services
- `Serilog` (+ `Serilog.Extensions.Hosting`, `Serilog.Settings.Configuration`, console/file sinks) — logging backend
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
                                        │ messages starting with the guild's
                                        │ command identifier (per guild)
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

A second hosted service runs alongside the Discord client: `ScheduledMessageDeliveryService<ScheduledReminderMessage>` (§3.8), the generic scheduled-message delivery engine — today delivering due reminders as direct messages.

### 2.1 Composition root — `HambotV2/Program.cs`

- Registers `appsecrets.json` as an additional configuration source.
- Replaces the default logging providers with Serilog (`builder.Logging.ClearProviders()` + `AddSerilog`), configured via `ReadFrom.Configuration` from the `Serilog` configuration section, so sinks and levels come from `appsettings.json` / environment variables (see §4).
- DI registrations:

| Service | Interface | Lifetime |
|---|---|---|
| `DiscordClient` | hosted service | singleton (hosted) |
| `DiscordSocketClient` | — | singleton — the shared gateway client, created at the composition root with the bot's gateway intents; used by `DiscordClient` and `ReminderDmSender` (§3.8), disposed by the container |
| `CommandService` | `ICommandService` | singleton (holds the `IScheduledDeliverySignal` it received from DI and passes it down the registration chain) |
| `GuildSettingsService` | `IGuildSettingsService` | singleton (caches per-guild command identifiers) |
| `CooldownService` | `ICooldownService` | singleton (shares the in-memory cooldown timestamps across messages) |
| `TimeProvider` | — | singleton (`TimeProvider.System`; injectable so tests advance time deterministically) |
| `CommandHandler` | `ICommandHandler` | transient (resolved per message, disposed after use) |
| `PermissionsService` | `IPermissionsService` | transient |
| `DatabaseContext` | concrete `DbContext` and `IDatabaseContext` | transient (both service and options lifetime) |
| `ScheduledDeliverySignal` (§3.8) | `IScheduledDeliverySignal` | singleton — the wake-up signal shared by the scheduled-message commands and every delivery loop |
| `ReminderMessageSource` (§3.8) | `IScheduledMessageSource<ScheduledReminderMessage>` | singleton — the reminder persistence behind the delivery engine |
| `ReminderDmSender` (§3.8) | `IScheduledMessageSender<ScheduledReminderMessage>` | singleton — delivers reminders as DMs on the shared gateway client |
| `ScheduledMessageDeliveryService<ScheduledReminderMessage>` (§3.8) | hosted service | singleton (hosted) — the generic delivery engine, hosted once per scheduled-message kind |
| `DiscordPresenceGateway` (§3.10) | `IPresenceGateway` | singleton — the seam over the shared gateway client the presence service talks to; subscribes the client's Ready event at construction |
| `PresenceService` (§3.10) | `IPresenceService` | singleton — the single path for activity, bio, avatar, and banner changes; resolved eagerly in `Program.Main` so its Ready subscription exists before the gateway starts |
| `ImageContentDownloader` | `IImageContentDownloader` | singleton — shared-`HttpClient` image downloads for the `!avatar`/`!banner` commands |

## 3. Component Design

### 3.1 Discord connection — `Connections/DiscordClient.cs`

A `BackgroundService` that:

1. Receives the `DiscordSocketClient` from DI — it is created at the composition root (`Program.cs`) as a shared singleton with only the gateway intents the bot uses: `Guilds | GuildMessages | DirectMessages | MessageContent` (explicit intents instead of `AllUnprivileged` avoid Discord.Net warnings for unused intents). `ReminderDmSender` shares the same client (§3.8); the DI container owns and disposes it.
2. Logs in with the token from configuration key `Secrets:BotToken` and starts the gateway.
3. Subscribes to `MessageReceived`. Messages from bots and webhooks (including the bot itself) are ignored — otherwise the bot would re-process its own replies that start with the identifier (e.g. the `!help` detail output). Pre-filter (FR-26): the *effective command identifier of the message's guild* — resolved from `IGuildSettingsService` (per-guild override, global default fallback) — decides which messages are command invocations; direct messages use the global default, since DM commands run in the DM scope (see 3.2). Matching messages are forwarded to a freshly resolved `ICommandHandler` (resolved from `IServiceProvider`, disposed after handling).
4. Bridges `Discord.Log` events to `Microsoft.Extensions.Logging` using the configurable `BotConfiguration:LogFormat` format string. Gateway connection events are logged explicitly: `Disconnected` at Warning (including the reason), `Connected` at Information (fires both for the initial connection and for Discord.Net's automatic reconnects).
5. Runs a keep-alive loop that logs gateway connection state every 5 minutes.
6. On host shutdown (or any exit path, including startup failures) stops the gateway and logs out, logging the shutdown ("Shutting down Discord connection..." → "Discord connection shut down cleanly.") instead of dying mid-connection; the DI container disposes the shared client afterwards, once the hosted services have stopped.

### 3.2 Command pipeline — `Commands/`

**`ICommand`** (`Commands/ICommand.cs`) — the command contract:

- `string Name` — must be lowercase (lookup lowercases input; registration rejects anything else).
- `string Description` — one-line summary shown in the `!help` list.
- `string Usage` — argument summary without the command identifier, e.g. `userpermission <user> [tier]`.
- `int DefaultPermissionTier` — required tier when no `CommandMetaData` override exists in the database (see 3.3).
- `Task ExecuteCommand(IMessage, ICommandContext)` — the command body. The pipeline operates on the `IMessage` interface (which `SocketMessage` implements) so the handler is unit-testable with mocks.

Help text lives on the command implementation itself, so it cannot drift from the code or the registration chain, and no parallel registry has to be kept in sync.

**`ICommandContext` / `CommandContext`** — a thin wrapper exposing the per-message `IDatabaseContext` to command implementations, keeping commands decoupled from DI. It also carries a `TierOverrides` record (see 3.3) — the message's guild owner plus the configured bot owner id — and `GetUserEffectiveTier(userId)` computes a user's effective tier so commands share the exact semantics the permission check uses (in the DM scope it returns owner tier without a database read). Per-guild scoping: the context additionally carries `GuildId` (the invoking guild; **null in direct messages**, exposed as `IsDirectMessage`), `CommandIdentifier` (the guild's effective identifier, or the global default in DMs; used by `!help` for display), and `GuildSettings` (the `IGuildSettingsService`, used by `!settings` to write guild settings with cache invalidation).

**`CommandHandler`** (`ICommandHandler`) — per-message orchestrator, transient and disposable:

1. **DM scope (FR-25)**: commands also run in direct messages. A null guild id marks the DM scope: the user acts at **owner tier (3)** — the configured bot owner at tier 4 — and commands require their **built-in default tiers**; no per-guild state is read or written, so DMs need no database rows and permissions cannot be adjusted from a DM. Commands that manage guild state (`!userpermission`, `!commandpermission`, `!settings`, the quote commands, the response-management commands) decline in DMs with "…only available in servers"; the reminder commands are user-scoped, not guild-scoped, and remain fully usable in DMs (§3.8, FR-71). The `DirectMessages` gateway intent stays enabled so DM commands are received at all.
2. Resolve the effective command identifier via `IGuildSettingsService` — the guild's identifier, or the global default in DMs — then the command via `ICommandService.TryGetCommand(content, identifier)`; identifier-only messages (a bare `!`) are ignored silently, unknown commands get "The requested command is unavailable". The unknown-command branch first consults the invoking guild's custom responses (§3.7, FR-10/FR-54) — through the same permission and cooldown checks — and falls back to the "unavailable" reply only on a miss; in direct messages the reply stays as-is (FR-59). A resolved command is first checked against maintenance mode (§5.2, FR-98): while a backup restore is in progress, every command except the restore command's own confirmation step is declined with a maintenance notice (audit outcome `MaintenanceMode`), and custom responses are gated the same way.
3. Check permissions via `IPermissionsService.CheckUserPermissions`, passing the guild id (null = DM scope) and a `TierOverrides` built from the guild owner (null in DMs) and the configured bot owner id; if insufficient, reply "You are not permitted to use that command". The check returns a `PermissionCheckResult` carrying the verdict *and* the user's effective tier, so the cooldown check (next step) resolves its window without repeating the database lookup — and the tier it uses is the guarded one, so the window cannot be bypassed via permission lookups.
4. Check the command cooldown via `ICooldownService.TryBeginInvocation` (see the cooldown component below); a throttled invocation is answered with "You're using commands too quickly — please wait N more seconds." and never executes the command.
5. Execute the command with a `CommandContext` wrapping the per-message `IDatabaseContext`, the guild id (nullable), the effective identifier, the settings service, and the permission context.

The whole pipeline is wrapped in a try/catch: on any failure (throwing command, database outage during the permission check, ...) the exception is logged with the command name and message content, and the user gets a generic in-channel error message ("An unexpected error occurred...") that never contains exception details. The failure is additionally posted to the operator notification channel when one is configured (§3.13, FR-118 — through the rate-limited publisher, never blocking the reply). Each failing message is handled independently — a single failure never takes the bot down, and subsequent messages keep being processed. On disposal the handler also disposes its per-message `DatabaseContext`, so connections are released after every message regardless of success or failure.

**Command audit event** (NFR-11) — `HandleCommand` emits exactly one structured Information-level event per handled message, in a `finally` block so every exit path is covered (including throws). The event carries named fields for filterability: `CommandName` (resolved name, null when unknown/identifier-only), `Outcome` (the `CommandOutcome` enum in `Commands/CommandOutcome.cs`: `Executed`, `DeniedPermission`, `UnknownCommand`, `Ignored` for bare-identifier messages, `Throttled` for cooldown rejections, `Failed`), `GuildId` (null in DMs), `ChannelId`, `UserId`, and `DurationMs` (handler wall time). Message content never appears in the audit event — only the Error-level failure log contains it — keeping routine usage logs free of user text. This single event replaces the former per-branch log lines, making command usage traceable from logs without debugging.

**`CommandService`** (`ICommandService`) — singleton command registry:

- Holds a `Dictionary<string, ICommand>` keyed by command name.
- Lazily populated on first lookup (`CommandsRegistered` flag) by calling `RegisterAllCommands.RegisterCommands`.
- `RegisterCommand` is the single chokepoint for the command contract: it rejects non-lowercase or empty names, missing `Description`/`Usage`, and invalid `DefaultPermissionTier` values, so a command without help text can never enter the registry.
- `TryGetCommand(content, identifier)` parses via the shared `CommandParsing` helper. Parsing rules: the command identifier — the *guild's effective identifier*, passed by the handler — is matched case-sensitively as an exact prefix of the trimmed message; any run of whitespace separates tokens; the command name is the first token after the identifier, lowercased. Bare identifier-only messages (e.g. `!` or `!   `) resolve to no command.
- `IsIdentifierOnlyMessage(content, identifier)` lets the handler distinguish "no command name given" (ignored silently) from "unknown command" (answered).

**Command registration** — a small static registration chain:

- `Commands/RegisterAllCommands.cs` → delegates to `Commands/SystemCommands/RegisterSystemCommands.cs`, `Commands/QuoteCommands/RegisterQuoteCommands.cs`, `Commands/ResponseCommands/RegisterResponseCommands.cs`, and `Commands/ReminderCommands/RegisterReminderCommands.cs`. The chain receives the shared `IScheduledDeliverySignal` (held by `CommandService`) so the reminder commands can wake the delivery loop.
- To add a command: implement `ICommand` (system commands live in `Commands/SystemCommands/`) and register it in the relevant `Register*Commands` class.

**Shared argument parsing — `Commands/CommandArguments.cs`** (NFR-13) — commands receive typed arguments instead of hand-rolled `Split`/`TryParse` code. `CommandArguments.Parse(content)` splits the raw message content on runs of spaces and drops the first token (identifier + command name), so index 0 is the command's first argument. Accessors: `Count`, `Get(index)` (raw token or null — commands echo it back in replies), `GetJoined(index)` (the remaining tokens joined with single spaces — free-form text like quote text or search terms), and the typed parsers `TryGetString`, `TryGetInt`, and `TryGetDiscordUserId` (raw id or `<@id>`/`<@!id>` mention, delegating to `DiscordUserId`). Quoting is not supported; one whitespace-separated token is one argument. All existing commands (`HelpCommand`, `CommandPermission`, `UserPermission`, `SettingsCommand`, the quote commands) are built on it; new commands must use it too instead of hand-parsing.

**Guild settings — `Commands/GuildSettingsService.cs`** (FR-26) — the singleton `IGuildSettingsService` resolves each guild's effective command identifier: its `GuildSetting` override if set, otherwise the global default (`BotConfiguration:CommandIdentifier`). Results are cached per guild in memory, and `SetCommandIdentifier(guildId, value, database)` (called by `!settings` with the command's own context) upserts the row *and* refreshes the cache, so the gateway pre-filter and command parsing — the two consumers, one source of truth — see a changed identifier on the very next message without a database roundtrip. Cache misses resolve a fresh transient context from a service-provider scope; the singleton never holds a `DbContext`.

**Command cooldowns — `Commands/ICooldownService.cs` + `Commands/CooldownService.cs`** (NFR-12) — a per-**(guild, user, command)** in-memory throttle. The singleton `CooldownService` holds a `ConcurrentDictionary<CooldownKey, DateTimeOffset>` of last-invocation timestamps; time comes from an injected `TimeProvider` (so unit tests advance the clock deterministically via `FakeTimeProvider`), and expired entries are removed opportunistically when their key is next checked, keeping the dictionary bounded by the recently used triples. The throttle key includes the command name, so a throttled `!hello` does not block `!help`. Direct messages run in the DM scope: their key uses the sentinel guild id 0 — no real guild has that id — so DM throttles resolve through the tier-3 window and never collide with any guild's. The cooldown window is resolved per tier, per guild, from the invoking user's **effective tier**: the guild's per-tier `GuildSetting` override (`CooldownSecondsTier0`–`Tier3`, via `GetCooldownOverride`) if present, otherwise the global per-tier default from `BotConfiguration:CooldownSecondsByTier` (3 seconds when the map or a tier entry is absent; `0` or negative disables throttling for that tier in that scope). **Tier 4 (bot owner) is never bound by a guild override** — it is a global, configuration-only role (FR-41) — so tier 4 always uses the global default; configurable tiers are 0–3. The check runs in `CommandHandler` *after* the permission check and *before* execution (see the pipeline steps above); a throttled invocation gets the user-friendly remaining-wait reply, is logged with `Outcome=Throttled`, and does not execute. Denied invocations are not throttled — they answer through the same cheap denial path as before.

### 3.3 Permissions — `Commands/PermissionsService.cs`

Tiered integer permission model with a named scale (FR-33):

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
- The **bot owner** configured under `Permissions:BotOwnerDiscordId` (in containers supplied via `BOT_OWNER_DISCORD_ID` in `.env` through compose) acts at tier 4 in every guild — the highest permission level of all, above guild owners. It is the designated tier for things that apply across guilds (FR-41), e.g. managing any guild's settings by running `!settings` there. The tier is configuration-only: the escalation guard math (strictly-below-own-tier) makes it unassignable through commands.
- `IPermissionsService` exposes `CheckUserPermissions` (returning a `PermissionCheckResult` with the verdict and the user's effective tier, which the pipeline's cooldown check consumes without a second lookup) plus the read accessors `GetUserEffectiveTier` and `GetCommandRequiredTier`, so the default semantics exist in exactly one place and `HelpCommand` reuses them. The implicit-tier ids travel as a single `TierOverrides` record (`Commands/TierOverrides.cs`), which also owns the effective-tier computation (`EffectiveTierOf`) — no method needs a parameter per override source.

**Escalation guard** (FR-36), enforced inside the permission-management commands:

- `!userpermission <user> <tier>`: the invoker can never modify their own tier, can only modify users whose effective tier is strictly below their own, and can only assign tiers strictly below their own. Target arguments accept raw ids and Discord mentions (`<@id>`, `<@!id>`).
- `!commandpermission <name> <tier>`: the invoker can only assign required tiers strictly below their own, and only to commands whose current required tier is strictly below their own — nobody can move a command gate to or past their own level.

Tiers are read from the database per message and managed at runtime via the two commands, so no code change or restart is needed to adjust access.

**Per-guild scoping** (FR-24): all `IPermissionsService` lookups take the guild id — user tiers are keyed `(GuildId, DiscordId)`, command tiers `(GuildId, CommandName)` — so a tier or override in one guild has no effect in any other. The permission-management commands act on the **invoking guild only** (the escalation guard compares tiers within that guild), and `!help` filters with the same per-guild semantics. The guild-owner bootstrap is per-guild by construction; the **bot owner** (tier 4, FR-41) remains globally effective in every guild as the designated cross-guild authority. A **null guild id means the DM scope** (FR-25): the user acts at owner tier (bot owner at tier 4), commands require their built-in defaults, and the lookups skip the database entirely — nothing is read or written.

### 3.4 Persistence — `Connections/DatabaseContext.cs`

EF Core `DbContext` against PostgreSQL with seven nested entity classes: five scoped per guild (FR-20/FR-24), one user-scoped (§3.8), and one global (§3.13):

| Entity | Key | Fields |
|---|---|---|
| `User` | `(GuildId ulong, DiscordId ulong)` | `PermissionTier` (int) |
| `CommandMetaData` | `(GuildId ulong, CommandName string)` | `PermissionTier` (int) |
| `GuildSetting` | `GuildId (ulong)` | `CommandIdentifier` (string?, null = global default applies, FR-26) and the per-tier cooldown overrides `CooldownSecondsTier0`–`CooldownSecondsTier3` (int?, used by the cooldown service; null = inherit the global default). The tier→column mapping lives on the entity (`GetCooldownOverride`/`SetCooldownOverride`) as the single source of truth for both the cooldown service and `!settings`. |
| `Quote` (§3.6, FR-42) | `(GuildId ulong, Number int)` | `Text` (1–2000 chars), `AddedByDiscordId` (ulong), `CreatedAt`. |
| `CustomResponse` (§3.7, FR-51) | `(GuildId ulong, Name string)` | `Content` (1–1000 chars), `LastModifiedByDiscordId` (ulong), `CreatedAt`, `UpdatedAt`. |
| `Reminder` (§3.8, FR-61) | `(OwnerDiscordId ulong, Number int)` | `Message` (1–500 chars), `DueAtUtc` (DateTimeOffset), `GuildId` (ulong?, null = created in a DM), `CreatedAt`, `FailedAtUtc` (DateTimeOffset?, null = pending). |
| `BotSetting` (§3.13, FR-117) | `Key (string)` | `Value` (string?) — global key-value settings, the global analog of `GuildSetting`; first key: the operator notification channel id (FR-117). |

Composite keys are configured in `OnModelCreating`. EF Core migrations live in `HambotV2/Migrations/`: `InitialCreate`, then `PerGuildScoping` (adds the `GuildId` dimension and the `GuildSettings` table), then `AddQuotes` (§3.6), then `AddCustomResponses` (§3.7), then `AddReminders` (§3.8), then `AddBotSettings` (§3.13). Pre-existing rows keep the sentinel `GuildId = 0` there — it matches no real guild id, so legacy tiers are inert; admins re-grant tiers per guild (guild owners already bootstrap at owner tier, FR-34). Regenerate with `dotnet ef migrations add <Name>` from the `HambotV2/` project directory; the `DesignTimeDatabaseContextFactory` lets the EF tools scaffold migrations without running `Program.Main` (which would attempt a real migration). Every migration is covered by the automatic startup migration (FR-30), so no manual step is introduced.

Migrations are applied automatically on startup: `Program.Main` runs `Database.Migrate()` before the Discord gateway starts. If the database is unreachable, a critical error is logged (naming the connection configuration) and the host exits with a non-zero code instead of starting a half-functional bot. No manual migration step is required.

There is also an `Connections/IDatabaseContext.cs` abstraction exposing the `DbSet`s and `SaveChangesAsync` — every §3.4 entity, the user-scoped `Reminder` included, is exposed on it the same way, keeping command and service code testable against the EF InMemory provider. The whole command pipeline (`CommandHandler`, `CommandContext`, `PermissionsService`) depends on `IDatabaseContext`, and `Program.cs` maps it onto the registered transient `DatabaseContext`. This is what lets the command tests run against the EF InMemory provider instead of PostgreSQL.

### 3.5 Commands currently implemented

All in `Commands/SystemCommands/`, registered by `RegisterSystemCommands`:

| Command | Name | Behavior |
|---|---|---|
| `HelloWorld` | `!hello` | Replies "Hello World". |
| `HelpCommand` | `!help [commandName \| pageNumber]` | Lists the commands available to the invoking user (required tier ≤ their effective tier), one line per command: identifier + name — description. The list is paginated by a character budget so no message exceeds Discord's 2000-character limit: `!help` shows page 1, `!help <pageNumber>` requests another page, and a multi-page list carries a `page n of m` header plus the page-hint footer; out-of-range pages get a friendly reply naming the valid range. With a non-numeric argument: detailed help (description, usage, required tier) for that command — only if the user can run it; unknown and insufficient-tier names get the same generic "The requested command is unavailable" reply, so the existence of higher-tier commands is never revealed (FR-40). |
| `CommandPermission` | `!commandpermission <name> [tier]` | With 1 arg: prints the command's required tier (database override, or a hint that the built-in default applies). With 2 args: sets/creates the command's tier, guarded by the escalation rules in 3.3. Admin-only by default. Acts on the invoking guild only; declines in direct messages (FR-25). |
| `UserPermission` | `!userpermission <user> [tier]` | With 1 arg: prints the user's effective tier (with a "guild owner" / "bot owner" note where applicable). With 2 args: sets/creates the user's tier, guarded by the escalation rules in 3.3. `<user>` accepts raw ids and `@mentions`. Admin-only by default. Acts on the invoking guild only; declines in direct messages (FR-25). |
| `SettingsCommand` | `!settings [commandidentifier <value>\|commandidentifier reset\|cooldown <tier> <seconds>\|cooldown <tier> reset]` | Manages the invoking guild's bot settings (FR-26). No args: shows the effective command identifier (server override or global default) and the server's cooldown overrides. `commandidentifier <value>` sets the guild's override (non-empty, ≤ 5 chars; cached so it applies to the next message), `commandidentifier reset` clears it back to the global default. `cooldown <tier> <seconds>` sets the guild's per-tier cooldown override (tiers 0–3 only; seconds ≥ 0, where 0 disables throttling for that tier), `cooldown <tier> reset` clears it back to the global default; the escalation guard mirrors FR-36 — an invoker may only configure tiers strictly below their own, so nobody can loosen the throttle that governs their own level, while the bot owner (tier 4, never bound by a guild override) may configure any tier. Admin-only by default; the bot owner can manage any guild by running the command there. Declines in direct messages (FR-25). |
| `VersionCommand` | `!version` | Replies with the bot's version — the release tag semver plus the build commit's short hash (§3.9, FR-76). Works in DMs. |
| `HealthCommand` | `!health` | Formats the health snapshot (§3.13) into one in-channel report (FR-105). Bot-owner tier by default; works in DMs. |
| `NotifyChannelCommand` | `!notifychannel [<channel> \| clear]` | Shows, sets, or clears the operator notification channel (§3.13, FR-117); the override persists in `BotSetting` across restarts, `clear` falls back to the `Notifications:ChannelId` configuration default. Bot-owner tier by default; works in DMs. |
| `PresenceCommand` | `!presence [<type> <text...> \| clear]` | Shows, sets, or clears the bot's activity (§3.10, FR-79): `playing`/`watching`/`listening`/`competing` plus a single-line text ≤ 128 chars. Bot-owner tier by default; works in DMs; not persisted across restarts. |
| `BioCommand` | `!bio [<text...> \| clear]` | Shows, sets, or clears the bot's profile "About Me" (§3.10, FR-113): raw text ≤ 190 chars, line breaks allowed, applied in every member guild. Bot-owner tier by default; works in DMs; not persisted across restarts. |
| `AvatarCommand` | `!avatar [<image-url> \| reset]` | Shows, sets, or resets the bot's profile picture (§3.10, FR-115): the image comes from exactly one attachment or an image URL, validated (PNG/JPEG/GIF, ≤ 10 MB) before the per-guild plus global apply. Bot-owner tier by default; works in DMs. |
| `BannerCommand` | `!banner [<image-url> \| clear]` | Shows, sets, or clears the bot's profile banner (§3.10, FR-116), with the same shape and semantics as `!avatar` but per guild only. Bot-owner tier by default; works in DMs. |

`PermissionPersistence` holds the shared "upsert tier" logic of the two permission commands.

### 3.6 Quotes — per-guild quote storage and recall *(FR-42–FR-50, FR-72–FR-75 / NFR-14)*

**Entity & persistence.** `DatabaseContext.Quote` (§3.4 table) with composite key `(GuildId, Number)`: `Text` (1–2000 chars), `AddedByDiscordId`, `CreatedAt`. Per-guild scoping is the key itself — every read and write filters by `GuildId`, so a quote in one guild is never visible, searchable, or removable in another (FR-24/FR-42). The `AddQuotes` migration is applied automatically at startup (FR-30).

**Sequential numbering (FR-43).** The per-guild number is a simple increasing index, **0-based like the old bot's index-based quote list** (the first quote is number 0, so the old numbering feels familiar): each new quote is assigned one greater than the guild's current maximum (`MAX(Number) + 1`; `0` for the first quote), so numbers go 0, 1, 2, … as quotes are added. Removal never renumbers the remaining quotes; if the highest-numbered quote is removed, the next quote added takes that top number (the assignment always follows the current maximum), so no extra counter state is needed.

**Commands** — `Commands/QuoteCommands/` (`QuoteCommand`, `SearchQuotesCommand`, `RemoveQuoteCommand`, `ImportQuotesCommand`), registered by `RegisterQuoteCommands` in the registration chain (§3.2). Lowercase names, `CommandArguments`-based parsing, `Description`/`Usage` help text, and pipeline behavior identical to existing commands (FR-49): runtime tier adjustment via `!commandpermission`, per-tier cooldown throttling, the audit event. All quote commands decline in direct messages with the established "only available in servers" message (FR-50). Free-form text (quote text, search terms) comes from `CommandArguments.GetJoined`, which joins the remaining tokens with single spaces.

| Command | Default tier | Behavior |
|---|---|---|
| `quote` (no args) | 0 (everyone) | Replies with a random quote of the invoking guild, prefixed with its number (FR-44). No quotes → a friendly "no quotes yet" message, not an error. |
| `quote <number>` | 0 | If the argument parses as a number in `long` range, replies with that exact quote; an unknown number is an in-channel error, never an exception or empty reply (FR-45). |
| `quote <text...>` | 0 | Any argument that does not parse as a number — including numeric-looking values outside `long` range — is added as a new quote (a single, immediate action) and the reply carries the assigned number; text over 2000 chars is rejected in-channel (FR-47). |
| `searchquotes <text...>` | 0 | Case-insensitive substring match (`text.ToLower().Contains(term.ToLower())` — works on both Npgsql and the InMemory test provider), number-prefixed, ordered by number, capped at 10 results; no matches → "no matching quotes"; empty term → in-channel error (FR-46). |
| `removequote <number>` | 1 — moderator (FR-48) | Removes the quote and confirms; unknown number → in-channel error. |
| `importquotes` | 4 — bot owner (FR-72–FR-75) | Imports the old bot's `quoteSettings.json` attached to the invocation message into the invoking guild (§3.6, legacy import). |

The random fetch draws the guild's quote count and skips a `Random.Shared`-chosen offset — one query per invocation and provider-agnostic for tests (NFR-14 asserts "a quote from the guild", not a specific one).

**Reply fitting.** Quote text is capped at Discord's 2000-character message limit (FR-47), but a maximal quote plus its number prefix — or a search result list of up to 10 quotes — would still exceed what one Discord message can carry. Every quote reply therefore goes through `QuoteCommand.SendFittedAsync`, which trims the reply to the 2000-char limit with a trailing ellipsis when it does not fit; the stored quote text is never modified, and fetches of maximal quotes never fail delivery.

**Legacy import — `!importquotes` (FR-72–FR-75).** The old bot stored its quotes globally in `quoteSettings.json` — `{"quotes": [{"quote": "...", "date": "..."}]}`. The import command is the migration path for that file into a guild:

- **Invocation**: run by the configured bot owner in the target guild with the JSON file attached to the message; the first attachment is read. No attachment → in-channel error, nothing stored. The command declines in DMs like the other quote commands (FR-50).
- **Permission gate**: the default required tier is 4 (bot owner) — through the standard pipeline, no special-casing. Tier 4 is reachable only via configuration (FR-37), so neither guild owners (tier 3) nor elevated admins can ever run it; it appears in `!help` only for the bot owner, like other commands above a user's tier (FR-40). The gate is also effectively immutable at runtime: `!commandpermission`'s escalation guard only touches commands whose current required tier sits strictly below the invoker's own tier, and nothing sits above tier 4 — so no `CommandMetaData` override for `importquotes` can ever be created.
- **Download**: the attachment is fetched from its Discord CDN URL through `Commands/AttachmentContentDownloader.cs` — an `IAttachmentContentDownloader` abstraction over a shared `HttpClient`, constructor-injected so unit tests supply a fake. A failed download produces an in-channel error and stores nothing.
- **Parsing**: a private DTO mirrors the legacy file (`quotes` array of `{ quote, date }`, the `date` optional); `System.Text.Json` matches the lowercase property names as-is. Malformed JSON or a file without quotes → in-channel error, nothing stored.
- **All-or-nothing validation** (FR-74): every entry must be non-empty and at most 2000 chars (FR-47) — the limit is per quote, identical to `!quote <text>`; the file itself has no size or quote-count limit, so a legacy file with hundreds of quotes — or long individual quotes — imports in one run. The whole file is validated before anything is stored; the first invalid entry aborts with an in-channel error naming its position.
- **Storage** (FR-73): entries are appended in file order, continuing the per-guild sequential numbering (FR-43 — `MAX(Number) + 1` at import start, one greater per entry; 0-based, so an empty guild imports starting at `#0`). `AddedByDiscordId` records the importing bot owner; `CreatedAt` takes the legacy `date` (UTC) when present, else the import message's timestamp, so the original quote dates survive the migration. The reply reports the count and the number range assigned.
- Re-running the import appends the file again (duplicates are the operator's responsibility); the command is idempotent per-invocation only.

### 3.7 Custom command responses — user-defined text commands, per guild *(FR-51–FR-60 / NFR-15)*

**Entity & persistence.** `DatabaseContext.CustomResponse` with composite key `(GuildId, Name)`: `Content` (1–1000 chars), `LastModifiedByDiscordId`, `CreatedAt`, `UpdatedAt`. Lookups are always by the full composite key — dispatch and management never read across guilds (FR-51). Migration: `AddCustomResponses`.

**Name rules (FR-52).** A single lowercase token of 1–32 characters matching `^[a-z0-9_-]{1,32}$`. The pattern lives in one shared helper (`Commands/ResponseCommands/CustomResponseNames.cs`) so `addresponse` (and any future write path) validates identically.

**Built-in precedence (FR-53).** Built-ins win by construction: `CommandHandler` resolves the built-in registry first and consults custom responses only after that fails, so a response can never shadow a built-in — including built-ins added later. `addresponse` additionally rejects names of currently registered built-ins with an in-channel error.

**Dispatch fallback (FR-54/FR-55).** In `CommandHandler`, between "built-in lookup failed" and the "unavailable" reply (§3.2, pipeline step 2):

- Direct messages keep the plain "unavailable" reply (FR-59).
- In a guild, the parsed command token is looked up as `(GuildId, name)` in `CustomResponse`; a miss falls through to the "unavailable" reply (FR-10).
- On a hit, the invocation goes through the *same* checks as a built-in, in the same order: the permission check — required tier = the `CommandMetaData` override for `(GuildId, responseName)`, default 0 (FR-19/FR-55) — then the cooldown, keyed on the response name so it throttles independently of any built-in. Denial and throttle replies are the built-in ones. `IPermissionsService` gains a second entry point taking `(name, defaultTier)` instead of an `ICommand`, so both dispatch paths share the exact tier semantics; `ICooldownService.TryBeginInvocation` already keys on a bare command name and needs no change.
- The audit event logs the response name as `CommandName` with outcome `Executed` — the one-event-per-message chokepoint (NFR-11) stays intact.

**Text-only content (FR-60).** `addresponse` invocations carrying any file/image attachment are rejected in-channel and store nothing; dispatched content is sent as a plain text reply. Links are ordinary text — stored verbatim, sent verbatim; any preview or embed is rendered by the Discord client, and the bot uploads no files itself.

**Management commands** — `Commands/ResponseCommands/`, registered by `RegisterResponseCommands`; all three decline in direct messages (FR-59):

| Command | Default tier | Behavior |
|---|---|---|
| `addresponse <name> <content...>` | 1 — moderator (FR-56) | Creates the response, or — if the name exists in the guild — updates `Content`, `LastModifiedByDiscordId`, and `UpdatedAt`; the reply distinguishes "created" from "updated". Empty content, content over 1000 chars, invalid names, built-in name collisions, and attached files are rejected in-channel. |
| `removeresponse <name>` | 1 — moderator (FR-57) | Deletes the response and confirms; unknown name → in-channel error. |
| `listresponses` | 0 (FR-58) | Lists the guild's response names alphabetically; a guild with none gets a message saying so. |

### 3.8 Reminders — user-scoped timed reminders *(FR-61–FR-71 / NFR-16)*

**Entity & persistence.** `DatabaseContext.Reminder` with composite key `(OwnerDiscordId, Number)`: `Message` (1–500 chars), `DueAtUtc` (always UTC), `GuildId` (null = created in a direct message), `CreatedAt`, `FailedAtUtc` (null = pending), and `RecurrenceInterval` (null = one-shot, §3.14). Reminders are user-scoped, not guild-scoped: the key carries the owner, so one user's reminders follow them across guilds and DMs (FR-61). Migration: `AddReminders`.

**Sequential per-user numbering.** As with quotes (§3.6): `MAX(Number) + 1` within the owner at creation — the number increases by one with each reminder added. `!reminders` shows these numbers, `!cancelreminder` targets them; cancelling never renumbers the remaining reminders.

**Duration parsing** — `Commands/ReminderCommands/ReminderDurationParser.cs`, a pure, unit-testable helper with no Discord or database surface (NFR-13 spirit: one shared parser instead of per-command parsing):

- Leading number+unit tokens: units `s`/`m`/`h`/`d`/`w` accepted case-insensitively, with common abbreviations (`sec`, `min`, `hr`, `wk`, and their plurals) and long forms (`second` … `week` and their plurals); compact (`10d 5h`) and spaced (`10 d 5 h`) forms both valid, multiple tokens summed (FR-62).
- The total must be positive; there is no minimum or maximum — durations of years are accepted (FR-63). Only the `TimeSpan` type itself caps the range (≈29,000 years); a total beyond it is treated as unparsable. Unparsable or non-positive input → in-channel error with a usage hint, and nothing is stored.
- The reminder message is the remainder after the duration tokens: non-empty, capped at 500 chars.

**Commands** — `Commands/ReminderCommands/`, registered by `RegisterReminderCommands`; all tier 0 and — being user-scoped — fully usable in direct messages (FR-71). Every query filters by `OwnerDiscordId`, so users can never see or cancel another user's reminders (FR-69).

| Command | Behavior |
|---|---|
| `remindme <duration> <message...>` | Persists the reminder (due = now + duration, UTC; guild id of creation, null in a DM) and confirms in-channel, echoing message and due time (FR-64). Enforces the per-user cap of 10 pending reminders — at cap, an in-channel error and nothing stored (FR-70). After saving, notifies the delivery signal so a newly earliest reminder re-schedules delivery. The `every <duration>` form creates a recurring reminder (§3.14). |
| `reminders` | Lists the invoking user's pending reminders (number, due time, message), soonest first, capped at 10 entries (FR-68). |
| `cancelreminder <number>` | Cancels one of the invoking user's own pending reminders and confirms; unknown or foreign numbers → in-channel error (FR-69). Notifies the delivery signal after deleting. |

**Delivery — the generic scheduled-message engine (`Scheduling/`) wired to reminders (`Reminders/`), FR-65–FR-67.** The delivery loop is not reminder-specific: `Scheduling/` holds a reusable engine generic over the message kind, and reminders are its first consumer — a future scheduled-message kind (announcements, follow-ups, ...) plugs in with its own source and sender and another hosted engine, reusing the timing and retry behavior unchanged.

- `Scheduling/ScheduledMessageDeliveryService.cs` — the hosted `BackgroundService`, hosted once per message kind as `ScheduledMessageDeliveryService<TMessage>` (today only `ScheduledMessageDeliveryService<ScheduledReminderMessage>` runs). All time comes from the injected `TimeProvider` (tests advance it via `FakeTimeProvider`); the engine itself never touches a database. Each pass: snapshot the wake-up signal, ask the source for the earliest pending due time, then sleep until `min(earliest due, now + 10 s)` — the bounded fallback poll covers clock drift and missed signals — through `Task.Delay` against the `TimeProvider` racing the signal; with nothing pending it waits on the signal alone. Waking (timer or signal), it hands every due message to the sender and repeats. Snapshotting the signal before the query guarantees a message scheduled right after the read still wakes the wait. Each delivery is isolated: a failure is retried a bounded number of times — 3 attempts with a 2-second backoff — then the source records the failure and it is logged (FR-67); one message's failure never blocks the others and never crashes the service.
- `Scheduling/ScheduledDeliverySignal.cs` — the singleton wake-up signal (a replaceable `TaskCompletionSource`), shared by all scheduled-message kinds: a notification wakes every loop, and each cheaply re-checks its own pending set. `remindme`/`cancelreminder` notify it after saving, so whenever a newly created (or removed) reminder becomes the earliest pending one, the loop wakes and re-schedules instead of sleeping to the old horizon (second-accurate wake-ups come from computing the delay against `TimeProvider`, not from polling faster, FR-65). The commands receive the signal through the registration chain — `CommandService` holds the DI singleton and passes it to `RegisterReminderCommands` (its parameterless constructor defaults to an isolated signal, keeping tests that do not exercise delivery self-sufficient).
- `Reminders/ReminderMessageSource.cs` — the reminder implementation of `IScheduledMessageSource<TMessage>`: earliest pending due time, the due list (earliest first), and the delivery outcomes. All database work goes through `IDatabaseContext` resolved from fresh `IServiceScopeFactory` scopes — the source never holds a `DbContext`. Delivered one-shot rows are deleted from the pending set (a delivered recurring row is re-scheduled instead, §3.14); rows that exhausted the retries are marked failed (`FailedAtUtc` set) and stay in the database for audit but leave the pending set. Each outcome is recorded in its own scope against the row as currently stored (re-fetched by owner and number), so a reminder cancelled mid-delivery is not resurrected.
- `Reminders/ReminderDmSender.cs` — the reminder implementation of `IScheduledMessageSender<TMessage>`: a direct message to the owner with the stored text verbatim, on the shared `DiscordSocketClient` — the gateway user cache first, the REST client as fallback (the cache does not know a user right after a restart); a user resolvable through neither is a delivery failure handled by the bounded retry.
- Restarts recover by construction (FR-66): the pending set is database state, so a reminder that became due while the bot was down is delivered on the service's first pass.

### 3.9 Version reporting — embedded build version and `!version` *(FR-76–FR-77 / NFR-17)*

**Version string.** The version is the semver of the most recent `v`-prefixed tag reachable from the built commit plus that commit's abbreviated (7-char) hash, as semver build metadata: tag `v1.2.3` at commit `1639324…` → `1.2.3+1639324` — also when the built commit *is* the tag, so every build is unambiguous. Fallbacks (FR-76): no reachable tag → `0.0.0+<hash>`; no git metadata at all → `0.0.0+unknown`. Tagging convention: C-5. The `v1.0.0` release tag exists, so builds since it report `1.0.0+<hash>`.

**Build-time embedding.** `HambotV2.csproj` contains an MSBuild target (`ComputeBotVersion`) that gathers the raw *pieces* — the tag via `git describe --tags --match "v*" --abbrev=0 HEAD`, the hash via `git rev-parse --short=7 HEAD` — and embeds them into the assembly as `AssemblyMetadata` attributes (`BotVersionTag`, `BotVersionCommit`), rather than composing the version string at build time. The raw pieces are embedded (not a composed string) so the tag→version formatting exists in exactly one place, the unit-tested `BotVersion.Format` (NFR-17). Both properties can be supplied externally (`-p:BotVersionTag=… -p:BotVersionCommit=…`) for builds without git; any git failure leaves them empty (stderr is redirected — `Exec`'s `ConsoleToMSBuild` captures it too, and git's "fatal:" diagnostics must not become the version), which the provider turns into the `0.0.0+unknown` fallback, never a build failure. The target hooks ahead of `GenerateAssemblyInfo`'s dependency chain (it must run before the input-cache hash that gates the generated file's regeneration; a plain `BeforeTargets="GenerateAssemblyInfo"` runs too late — after `CoreGenerateAssemblyInfo` has written the file). Local and Jenkins agent builds get the real values for free (the workspace has `.git` and fetched tags); the Docker build *stage* cannot (`.dockerignore` keeps `.git` out of the build context), so every container build entry point passes the pieces in (deployment wiring below).

**Runtime provider.** A small internal static `BotVersion` provider reads the embedded metadata from its own assembly once (the metadata lives in the bot assembly, not the entry assembly, so the value is correct however the bot is hosted) and exposes the formatted string, `Current`. The pure `Format(tag, commitHash)` strips the tag's `v` prefix and applies the FR-76 fallbacks; it is separately unit-tested (NFR-17). `Current` is the single source for the `!version` command and for one startup log line — `Bot version: <version>`, logged in `Program.Main` right after the host is built, before configuration validation, so even a misconfigured start identifies the exact build. That line is what the deploy verification matches (§5.1); the command is the only user-facing version surface (the presence feature, §3.10, is presence text — the version there is bot identity, not version reporting).

**`!version` command (FR-77).** `Commands/SystemCommands/VersionCommand.cs` (class `VersionCommand` — a plain `Version` collides with `System.Version` at the use sites) implementing `ICommand`: `Name = "version"` (lowercase, C-4), `DefaultPermissionTier = 0`, `Description`/`Usage` per FR-39, registered in `RegisterSystemCommands`. `ExecuteCommand` replies in-channel with the version string; no guild state is touched, so direct messages work (FR-25).

**Deployment wiring (extends §5).**

- `HambotV2/Dockerfile`: `ARG BOT_VERSION_TAG` + `ARG BOT_VERSION_COMMIT` (re-declared in the build and publish stages), passed to both `dotnet build` and `dotnet publish` as `-p:BotVersionTag`/`-p:BotVersionCommit`.
- `docker-compose.yml`: the `bot` service's `build.args` maps both from the environment (`BOT_VERSION_TAG`, `BOT_VERSION_COMMIT`); plain `docker compose up --build` without them falls back to `0.0.0+unknown`.
- `scripts/deploy.sh`: reads the same two git commands for the exact commit it is deploying (in the server's clone, already checked out at that commit) and exports both before `docker compose build`; the startup verification additionally requires a `Bot version: <version>` log line matching the expected tag + commit (composed from the same pieces with the same fallbacks), so a mis-versioned build rolls back like any other failed deploy. The running version is also recorded in `.deploy-info`.
- `Jenkinsfile`: nothing to pass — the agent build runs in a workspace with `.git` and fetched tags, so the tested binary is stamped for free and the deployed image gets the identical pieces from `deploy.sh`.

### 3.10 Discord presence & profile identity *(FR-78–FR-80, FR-113, FR-115–FR-116 / NFR-18)*

**Presence service.** A singleton `IPresenceService` (`Connections/PresenceService.cs`, interface in `Connections/IPresenceService.cs`) is the single path for changing the bot's Discord identity — startup configuration (FR-78/FR-113), the `!presence`/`!bio`/`!avatar`/`!banner` commands, and (later) the LLM presence job (§3.12) and the MCP `set_presence`/`set_bio` tools (§3.11) all converge on it:

- All applies run under one single-flight `SemaphoreSlim`, so concurrent sources never interleave Discord calls — the last request wins (FR-80). The desired state is one record swapped atomically, so the `CurrentActivity`/`CurrentBio` properties are consistent lock-free reads.
- Before the gateway is ready the service records the request and applies it on the gateway's Ready event, so startup configuration and any early runtime change both land correctly regardless of gateway timing. The service subscribes at construction and is resolved eagerly in `Program.Main` (before the hosted services start), so the Ready event cannot be missed.
- A failed apply (e.g. a Discord error mid-reconnect) is logged and never thrown out of the service; every apply method returns success, so the command path surfaces a failure as the standard in-channel error (FR-80). A failing guild in a per-guild apply never stops the remaining guilds from being attempted.
- The bio, avatar, and banner only become "managed" once configured or changed at runtime; an absent startup configuration leaves the Discord-side state untouched — a restart never wipes a hand-set bio (FR-113).

**Gateway seam.** The service talks to Discord through `IPresenceGateway` (`Connections/IPresenceGateway.cs`) — a narrow slice of the gateway client (activity, per-guild member profile, global avatar, current image URLs, the Ready event) so the service and the commands are unit-testable against a mock instead of a live connection (NFR-18). The production `DiscordPresenceGateway` wraps the shared `DiscordSocketClient` (§2.1). The roadmap spike verified that Discord.Net 3.20.2 natively supports everything: `IGuild.ModifyCurrentUserAsync` with `SelfGuildUserProperties` (`Bio`/`Avatar`/`Banner`, all clearable as specified-null) implements the September 2025 per-guild *modify current member* route (`PATCH /guilds/{guild.id}/members/@me`), and `ISelfUser.ModifyAsync` implements the long-supported global user route for the avatar. No raw REST fallback was needed, and there is no global banner route for bots — banners travel the member route only.

**Startup configuration (FR-78/FR-113).** An optional `BotConfiguration:Presence` section (`Type`: `playing` | `watching` | `listening` | `competing`; `Text` ≤ 128 chars) sets the initial activity once the gateway is ready; absent → no activity (Discord's default). The optional `BotConfiguration:Bio` (≤ 190 chars, line breaks allowed) sets the initial bio the same way; absent → the bio is left untouched. Parsing and validation are pure (`Connections/PresenceConfiguration.cs`, `PresenceStartupConfiguration.TryParse`): an invalid type, a half-configured pair, or over-limit text collects errors naming the offending key, and `Program.Main` fails fast with a critical log line naming them (the FR-32 style). Streaming activities are excluded (they require a stream URL).

**`!presence` command (FR-79).** `Commands/SystemCommands/PresenceCommand.cs`, registered by `RegisterSystemCommands`: no args → report current type + text (or "No activity is set."); `!presence <type> <text...>` → validate and apply through the service; `!presence clear` → remove. Default required tier 4 (bot owner) — the presence is a global, cross-guild bot identity, the same rationale as `!importquotes`' tier-4 gate. It touches no guild state, so it works in direct messages (FR-25). Runtime changes are not persisted: a restart reapplies the startup configuration.

**Profile bio (FR-113).** The `!bio` command (`Commands/SystemCommands/BioCommand.cs`: show / `<text...>` / `clear`, tier 4, DM-usable) manages the bio through the same service. Because bots set their bio through Discord's per-guild member route, the service applies it in every guild the bot is a member of — a bot's identity is one, expressed per guild. The bio text is the raw remainder of the message via the new `CommandArguments.GetRaw` (NFR-13): internal line breaks are preserved verbatim, unlike `GetJoined`'s collapsed joins; non-empty and ≤ 190 chars are validated in-channel. Runtime changes are not persisted (a restart reapplies the configured bio when one is set, and never wipes a hand-set one).

**Avatar & banner (FR-115/FR-116).** The bot's profile picture and banner are managed through the same service: the avatar applies per guild through the member route plus the global avatar through the user route; the banner per guild only (no global banner route for bots — spike). The `!avatar` and `!banner` commands (`Commands/SystemCommands/AvatarCommand.cs` and `BannerCommand.cs`, sharing the `ProfileImageCommandBase` flow) take the image from exactly one message attachment or a single image URL argument: both paths download through the injectable `IImageContentDownloader` (a shared `HttpClient` in production — the `IAttachmentContentDownloader` pattern of §3.6, keeping the commands unit-testable with a fake); the downloaded bytes are validated by `ProfileImages` before anything is applied — PNG/JPEG/GIF signature sniffing, never the client-declared content type, within Discord's 10 MB image limit — with in-channel errors otherwise. `!avatar reset` and `!banner clear` remove the custom images (the avatar falls back to the application default). No-argument invocations reply with the current image's CDN URL (`CurrentAvatarUrl` from the bot's global user, `CurrentBannerUrl` from any member guild — the service keeps the per-guild images identical). Both commands are tier 4, DM-usable; there is no startup configuration for images, and a restart leaves the Discord-side state untouched. Odysseus can set images too, without a dedicated MCP tool: `run_command` with the URL form (`!avatar <image-url>`) — attachments cannot exist on a virtual invocation.

**Tests (NFR-18)** — `BotTests/TestPresence.cs` (configuration parsing and validation including both boundaries, the service against a fake gateway: startup apply on Ready, deferred apply, single-flight serialization with last-write-wins, the apply-failure path including partial per-guild failures, per-guild applies, not-persisted-across-restarts semantics; the `!presence`/`!bio` commands: show/set/clear with in-channel validation errors, tier-4 gate, DM availability) and `BotTests/TestProfileImages.cs` (image validation, the `!avatar`/`!banner` attachment and URL paths, non-image and oversized rejection, ambiguous invocations, `reset`/`clear`, failure replies).

### 3.11 MCP server — Odysseus integration **(planned)** *(FR-86–FR-91 / NFR-20)*

**Hosting & transport.** The bot hosts an MCP server over streamable HTTP in-process: the worker references the ASP.NET Core shared framework and starts Kestrel, with the official `ModelContextProtocol` C# SDK mapping the session endpoint at the configured path. Configuration: `Mcp:Enabled` (default false), `Mcp:ListenUri` (default `http://0.0.0.0:2525`), `Mcp:ApiKey` (secret). The endpoint is active only when enabled with a key; the startup log names the state and the listen URI (FR-86). In compose the port is published loopback-only (`127.0.0.1:2525:2525`), so an agent on the deployment host can reach the endpoint while the internet cannot.

**Authentication (FR-87).** A bearer-key check in front of the endpoint: every request must carry the configured `Mcp:ApiKey`; anything else is rejected before any tool logic runs. The key travels the existing secret paths (`appsecrets.json` locally, `Mcp__ApiKey` in containers — NFR-3/NFR-4).

**Tool surface.** All tools are thin `internal` wrappers over existing components — nothing re-implements command or data logic:

| Tool | Backed by |
|---|---|
| `bot_status` | `BotVersion.Current` (§3.9), gateway state, connected guild count, `IPresenceService.Current` presence and bio (§3.10) |
| `list_guilds` | the shared client's guild cache |
| `get_recent_messages(channelId, limit)` | Discord REST (`GetMessagesAsync`), capped at 50, within the bot's existing channel access |
| `send_message(channelId, text)` | plain-text channel send, ≤ 2000 chars, no files/embeds (consistent with FR-60) |
| `set_presence(type, text)` | `IPresenceService` (§3.10), the same validation and limits as `!presence` |
| `set_bio(text)` | `IPresenceService` (§3.10), the same validation and limits as `!bio` (FR-113) |
| `run_command(guildId, commandLine)` | the real command pipeline via virtual invocation (below) |

**`run_command` — virtual invocation (FR-90).** Rather than duplicating feature logic as typed tools, `run_command` feeds the existing pipeline a *virtual* message: a double implementing `IMessage` with the given command text, the invoking user set to the bot's own Discord id, the given guild id, and a capturing `IMessageChannel` double that records `SendMessageAsync` text instead of posting to Discord (the test suite already doubles this surface, so the pattern is proven). The seam now exists as-built — the slash surface (§3.15) built `IVirtualCommandRunner` (`Commands/VirtualCommandRunner.cs`, with `VirtualCommandMessage` and the capturing channel) for exactly this reuse; the MCP endpoint only wraps it with the bot's own user and tier-4 authority. `CommandHandler` handles it like any message: identifier resolution, built-in/custom-response dispatch, permission check (the virtual user is granted tier 4 through the `TierOverrides` mechanism — the MCP endpoint is the operator's agent, trusted like configuration), cooldown check (keyed on the bot's own id, so tool-call floods throttle), execution, and the audit event (NFR-11). The captured reply text is returned as the tool result; nothing is posted to a real channel — posting is `send_message`'s job. Every future command is exposed to Odysseus for free, with semantics identical to a chat invocation.

**Auditing & failure isolation (FR-91).** Each tool call logs one structured event (tool name, outcome) without message content in routine events, mirroring NFR-11. A failing tool call is logged and returned as an error result; it never crashes the endpoint or the bot.

**Tests (NFR-20)** — without a live transport or Discord: bearer rejection; each tool over fakes (EF InMemory via `IDatabaseContext`, fake REST/senders, mocked client); and `run_command`'s reply capture, tier-4 authority, cooldown throttling, and audit semantics.

### 3.12 LLM integration, phase 1 — presence updates **(planned)** *(FR-92–FR-95 / NFR-21)*

**LLM client (FR-92, FR-95).** `Llm/ILlmClient.cs` abstracts a chat completion ("given a prompt, return text"); `Llm/MistralLmClient.cs` implements it against the Mistral API (`chat/completions`) with the key from `Llm:ApiKey` and the model from `Llm:Model` (default `mistral-small-latest`). Keys travel the existing secret paths (NFR-3/NFR-4). Without a configured key every LLM feature is simply inactive — the bot runs fully LLM-free.

**Presence generation job (FR-93).** `Llm/PresenceUpdateService.cs` — an opt-in hosted `BackgroundService` (`Llm:PresenceUpdates:Enabled`, default false). Each tick (interval `Llm:PresenceUpdates:IntervalMinutes`, default 60) it builds a prompt from the configurable persona seed (`Llm:Persona`, e.g. a hamster-themed companion persona), the bot's name and version, the current date/time, and the current presence; asks for a single-line activity text ≤ 128 chars; validates the output (non-empty, single line, ≤ 128 chars after trimming — over-limit or unparsable output counts as a failure, FR-94); and applies it through `IPresenceService` (§3.10) — the same serialized path as `!presence`, so the LLM can never interleave a partial update. Failures keep the current presence and retry on the next tick (FR-94). The loop uses the injected `TimeProvider` like the reminder engine (§3.8), so tests advance it deterministically. When `Llm:BioUpdates:Enabled` (default false) is also set, the same tick generates and applies a bio (FR-114) — non-empty, ≤ 190 chars, line breaks allowed, from the same persona seed — with identical validation, failure handling, and budget accounting.

**Persona (FR-99).** The bot ships with a documented default persona seed — its voice and character — used when `Llm:Persona` is unset. Authoring and tuning that persona is part of the feature work, not an afterthought (see the roadmap step): the default is drafted alongside the prompt construction, then tuned against real presence outputs, and lives in the repository as a documented, configuration-overridable default.

**Cost & usage guardrails (FR-102–FR-104).** The LLM client wraps every API call in an accounting scope (feature + UTC day → requests, tokens in/out), so `!llm status` and `!health` (§3.13) report real usage. A configurable daily request budget (`Llm:DailyRequestBudget`, default 500) hard-stops API calls for the rest of the UTC day once exhausted — features treat the stop like any other failure (FR-94): skip, change nothing, log; the watchdog (§3.13) alerts once. Accounting is deliberately in-memory: restarts are operator actions (deploys), and the watchdog makes a restart-looping bot loudly visible, which bounds the reset exposure. The `!llm` command (`Llm/LlmCommand.cs`, registered via the chain) is the runtime kill switch: `status` / `off` / `on`, tier 4 by default, DM-usable; the switch state is in-memory too, so a restart restores the configured state — a safe default that can never wedge the bot in "off" permanently by accident.

**Later phases (roadmap placeholders, not designed yet).** Conversation participation (per-guild opt-in, bounded cadence, reading recent messages via REST, replying as the bot) and memory (per-guild conversation summaries and remembered facts, new tables) build on this client and the MCP endpoint (§3.11); see [`roadmap.md`](roadmap.md). Phase 1 deliberately implements only presence generation.

**Tests (NFR-21)** — a fake `ILlmClient`, no network: prompt construction; output validation at the 128-char boundary and for empty/multi-line output; interval scheduling via `FakeTimeProvider`; the failure path (error → presence unchanged, next tick retries); and the feature being inactive without a key.

### 3.13 Watchdog, health reporting & operator notifications *(FR-105–FR-106, FR-117–FR-118 / NFR-23)*

**Health snapshot.** A singleton health provider (e.g. `Watchdog/HealthReport.cs`) aggregates the bot's operational state from existing components — nothing is re-probed ad hoc: build version (`BotVersion`, §3.9), uptime and gateway state (the `DiscordClient` service), a cheap database reachability probe, the pending reminder count (§3.8), the configured notification channel (below), and the backup status (below). The not-yet-landed fields (LLM state and usage, §3.12; MCP endpoint state, §3.11) join the snapshot when their phases land — until then the report shows only what is actually running. The current presence and bio (§3.10) are deliberately not part of the report: the profile commands and `IPresenceService.CurrentActivity`/`CurrentBio` own that surface. Two consumers share this single snapshot:

- **`!health` command** — `SystemCommands/HealthCommand.cs`, tier 4 by default, DM-usable, no guild state: formats the snapshot into one in-channel report (FR-105).
- **Watchdog service** — `Watchdog/WatchdogService.cs`, a hosted loop on the injected `TimeProvider` (interval `Watchdog:CheckIntervalMinutes`, default 5) evaluating the degradation conditions (FR-106): gateway down past `Watchdog:GatewayDownAlertMinutes` (default 10); backup trouble read from the shared status file — failed validation, overdue (interval plus grace), or a failed/overdue restore test (§5.2); the LLM daily budget exhausted or three consecutive LLM failures (FR-94/FR-103); maintenance mode (§5.2); and the notification channel being unreachable (below). (The LLM conditions stay dormant until §3.12 lands.) The backup-overdue check allows a grace window (default 60 minutes) on top of the configured interval. Each condition alerts on its rising edge, re-alerts at most every `Watchdog:ReAlertMinutes` (default 60) while it persists, and announces recovery once — by direct message to the configured bot owner on the shared gateway client (the same DM path reminders use, §3.8) *and*, when a notification channel is configured, as a post there through the notification publisher (FR-106/FR-118). The DM remains the out-of-band path for the case where the bot was kicked from the guild or the channel was deleted; without a configured owner the alerts degrade to logs. A failing check is logged, never propagated.

**Notification channel & publisher (FR-117/FR-118).** The bot posts errors, failures, and critical conditions to one operator-configured channel. The channel id lives in a new global `BotSetting` entity (§3.4 — key-value, the global analog of `GuildSetting`), with the optional configuration key `Notifications:ChannelId` as the default when no runtime override exists; `!notifychannel` (`SystemCommands/NotifyChannelCommand.cs`: show / `<channel mention or id>` / `clear`, tier 4, DM-usable) manages it at runtime with persistence. `Watchdog/NotificationPublisher.cs` is the single component that formats and posts notifications on the shared client: watchdog alerts and recoveries (above), command execution failures (the FR-15 error path), reminder deliveries that exhausted their retries (FR-67), maintenance-mode entry/exit and restore outcomes (§5.2), and its own delivery failures (which additionally DM the owner — the meta-failure the watchdog then tracks as "notification channel unreachable"). Posts are rate-limited (`Notifications:MaxPostsPerMinute`, default 5) with identical consecutive messages collapsed, so an error storm — a database outage failing every command — cannot flood the channel; the overflow is still fully present in the logs. A failed post is logged and never affects the originating feature. Startup criticals stay log-only: the gateway is not connected yet, which is what the deploy verification (§5.1) covers.

**Backup status file.** The backup sidecar (§5.2) writes a small JSON status file (`backups/.backup-status`: last successful backup, last validation result, last restore-test result) into the shared volume after every pass — the single channel through which the in-bot health surface sees the backup machinery without sharing a database with it (FR-100/FR-101 feed `!health` and the watchdog through it). The sidecar seeds its in-memory status from an existing file on startup, so a container restart cannot erase the last observed backup/restore-test state; correspondingly, the watchdog grants a never-recorded restore test the same interval-plus-grace window (measured from the file's last update) before calling it overdue — a restart or fresh deployment cannot trip a false alert.

**Tests (NFR-23)** — fakes + `FakeTimeProvider`: each condition's threshold detection, the alert-once/re-alert/recovery cadence, delivery via a mocked sender to both the owner DM and the notification channel, the no-owner log fallback, the `!health` report's fields and tier gate, notification posting per event type with the rate limit and identical-message collapse, and `!notifychannel`'s show/set/clear with persistence.

### 3.14 Recurring reminders *(FR-107–FR-109 / NFR-24)*

Extends §3.8 — the same entity, commands, and delivery engine:

- **Entity**: `Reminder` gains a nullable `RecurrenceInterval` (TimeSpan?; null = one-shot). The `AddReminderRecurrence` migration adds the column; every existing row stays one-shot.
- **Creation (FR-107)**: `remindme` recognizes the leading `every` token (case-insensitively, like the duration units); the interval reuses the duration parser (FR-62 — `ReminderDurationParser.TryParse` gained a start-index overload so the parse begins after the token) and must clear the configurable minimum (`Reminders:MinRecurrenceMinutes`, default 10) — shorter intervals are rejected in-channel. First due = now + interval; the confirmation reply states the recurrence (`Recurring reminder #N set for …, repeating every <interval>:`).
- **Redelivery (FR-107)**: on delivery, the source re-schedules a recurring row (`DueAtUtc = actual delivery time + interval`, read from the injected `TimeProvider` at record time) instead of deleting it — scheduling from the delivery time means downtime shifts the schedule instead of piling up a burst of catch-up deliveries. The bounded-retry failure logic (FR-67) applies per occurrence: a recurring row that exhausts its retries stops repeating and is marked failed like a one-shot (the row keeps its interval for the audit trail).
- **Management (FR-108)**: numbers are stable across deliveries (the row persists); `!reminders` marks recurring entries with their interval (`— repeats every 12h`); `!cancelreminder` deletes them permanently.
- **Caps (FR-109)**: a separate per-user cap (`Reminders:MaxRecurring`, default 5) with an in-channel error; recurring rows never consume one-shot slots (FR-70), so a user can hold ten one-shot plus five recurring reminders. Both caps are counted independently (`RecurrenceInterval == null` vs `!= null`).
- **Configuration**: the two keys live in the optional `Reminders` section; `ReminderSettings.FromConfiguration` (`Commands/ReminderCommands/ReminderSettings.cs`) falls back to the defaults for absent or invalid values — the §3.4 invalid-entries-are-absent treatment (`MaxRecurring` of 0 is valid and disables the recurring form). The settings flow through the registration chain: `CommandService` parses them from the injected `IConfiguration` and hands them to `RegisterReminderCommands`, so `RemindMeCommand` works with the defaults when no configuration is supplied (tests). Intervals are echoed back to users through `ReminderIntervalFormat` (non-zero day/hour/minute/second components, largest first).

**Tests (NFR-24)** — §3.8's testing style (EF InMemory, `FakeTimeProvider`): `every` parsing across the duration forms and the floor, invalid input, the creation reply, multi-cycle redelivery with schedule-from-delivery (no burst after simulated downtime), numbering stability, list marking, cancellation, the separate cap, one-shot independence, and the configuration fallbacks.

### 3.15 Discord application commands — slash commands *(FR-110–FR-112 / NFR-26)*

**Scope & registration (FR-110).** Opt-in (`SlashCommands:Enabled`, default false). The slash surface covers only two command families: **administrative/configuration commands** (permissions, guild settings, bot profile and identity, backups and restore, health, notification channel) and **commands that act solely on the invoking user's own state** (the reminder commands). Community-facing chat commands — quotes, custom responses, `!help`, `!version` — stay text-only: they live in the public chat flow rather than on an operator's configuration surface, and excluding them keeps Discord's `/` picker small and focused. (The planned `!llm` command will declare eligibility the same way when the LLM phase lands.)

Eligibility is declared explicitly on each command implementation through `ICommand.ApplicationCommand` — an `ApplicationCommandDeclaration` (`SlashCommands/ApplicationCommandMetadata.cs`) that also carries the options: typed options (`userpermission` and `commandpermission` take them — a user picker plus an optional integer tier, `cancelreminder` a typed number), the single free-form `arguments` fallback (settings, presence, bio, avatar, banner, notifychannel, restorebackup, remindme — so the argument line parses exactly as on the text surface, the recurring `every` form included), or none at all (health, backup, backups, reminders). The registration pass runs on the first gateway Ready (`SlashCommandService`): `ApplicationCommandDefinitionFactory` builds one definition per eligible command from the same registry (§3.2) — name and description from `ICommand` — and validates Discord's registration limits (names 1–32 lowercase, descriptions and option descriptions ≤ 100 characters, required options before optional ones, ≤ 25 options), so an invalid declaration fails the pass loudly instead of registering garbage. Definitions are pushed as one bulk global overwrite via the REST client, idempotent on every boot; a failed registration logs critical and leaves the text surface fully working. Custom responses are not registered (they are user-defined text, not registered commands).

**Execution (FR-111).** The interaction handler (`SlashCommandService`) converts the invocation into its equivalent text command line (`SlashCommandLineBuilder`: typed option values join in declaration order — text verbatim, integers invariantly, user and channel options as the mention forms the parsers accept — and the free-form option becomes the whole argument line), then feeds the real `CommandHandler` through the shared virtual-invocation seam (`Commands/VirtualCommandRunner.cs`): a `VirtualCommandMessage` carrying the interaction user, channel, and timestamp, with a capturing channel for the reply. The same seam is what the MCP `run_command` tool (§3.11) consumes. Identical semantics follow for free: the guild's effective identifier prefixes the virtual line so resolution matches the text surface, permission tiers per user and guild (including the DM scope for DM interactions, FR-25), cooldown keys shared with the text surface (one throttle per user per command, regardless of surface, NFR-12), audit events (NFR-11), and the generic error reply without exception details (FR-15).

**Replies (FR-112).** The interaction is acknowledged immediately with a public defer (commands may take a moment); the captured replies are then posted as followups: the command's normal output publicly with text identical to the text surface, while the pipeline's denials — permission, cooldown, maintenance, and the generic error — are sent as ephemeral responses only the invoker sees. Ephemerality is decided by the pipeline outcome (`CommandHandler` returns it): anything but `Executed` is ephemeral; replies the command itself emits — success output and argument-validation errors alike — are always public and bit-identical to the text surface, so command behavior never differs between surfaces. The text surface is unchanged — every command stays invokable as text; `!help` (itself text-only) documents the command set regardless of surface.

**Tests (NFR-26, `BotTests/TestSlashCommands.cs`)** — without live Discord: the interaction→command-line mapping (typed options, mention forms, the `arguments` fallback, required/optional, unmappable values), pipeline semantics through the runner (shared permission gate and denial text, shared cooldown key, guild identifier resolution, DM scope, generic error outcome, invocation-timestamp scheduling), denial ephemerality vs. public output, and the registration contract (the eligible set is exactly the administrative/configuration and user-scoped commands, every eligible command yields a valid definition, invalid declarations are rejected, community-facing commands yield none).

## 4. Configuration

- `appsettings.json` (committed, non-secret): `BotConfiguration:BotName`, `BotConfiguration:LogFormat`, `BotConfiguration:CommandIdentifier` (default `!`), `BotConfiguration:CooldownSecondsByTier` (global per-tier cooldown windows in seconds, e.g. `{ "0": 3, "1": 3, "2": 2, "3": 1 }`; missing entries default to 3 seconds and `0` disables throttling for that tier — in containers override per tier via `BotConfiguration__CooldownSecondsByTier__0` etc.; invalid or negative entries are treated as absent), plus the `Serilog` section.
- **Logging**: Serilog replaces the host's default logging providers (registered in `Program.cs` via `ClearProviders()` + `AddSerilog`, configured with `ReadFrom.Configuration`). The `Serilog` section defines the minimum level (with per-namespace overrides, e.g. `Microsoft`/`System` at Warning) and the sinks: Console and a daily-rolling File sink to `logs/hambot-.log` with 31 days retention (the `logs/` output is gitignored; in containers it lives in the container filesystem — mount a volume if retention matters). The level is adjustable at deploy time via `appsettings.json` or environment variables without a rebuild; `appsettings.Development.json` raises the default to Debug for local development. `BotConfiguration:LogFormat` is unrelated to the sinks: it remains the format string for bridging Discord.Net's `Discord.Log` events (§3.1). All bot code keeps using `ILogger<T>`; Serilog is an implementation detail of the composition root.
- `HambotV2/appsecrets.json` (gitignored, optional): `Secrets:BotToken` (Discord bot token) and `ConnectionStrings:DefaultConnection` (PostgreSQL connection string). Loaded explicitly in `Program.cs` for local development. Its expected structure is documented in `README.md`. Secrets must never be committed.
- Environment variables (e.g. `Secrets__BotToken`, `ConnectionStrings__DefaultConnection`) are the primary source in containers; `Program.cs` re-adds the environment provider after the secrets file so env vars take precedence. `appsecrets.json` is excluded from the Docker build context (`.dockerignore`), so the published image is secret-free.
- Optional key: `Permissions:BotOwnerDiscordId` (`BOT_OWNER_DISCORD_ID` in `.env`, wired through docker-compose) — the bot owner, holding the globally highest tier (4). Unset by default; guild owners always act at owner tier without any configuration.
- Required keys (`Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, `BotConfiguration:BotName`, `BotConfiguration:LogFormat`, `BotConfiguration:CommandIdentifier`) are validated at startup (`ConfigurationValidator`): any missing key is logged by name and the host exits with a non-zero code before any connection attempt.
- *(§3.10 / FR-78, FR-113)* `BotConfiguration:Presence` — optional: `Type` (`playing`/`watching`/`listening`/`competing`) and `Text` (≤ 128 chars); the startup activity, applied when the gateway becomes ready. Invalid values fail fast at startup. `BotConfiguration:Bio` — optional: the startup "About Me" text (≤ 190 chars, line breaks allowed); when absent the bio is left untouched at startup.
- *(planned, §3.11 / FR-86–FR-87)* `Mcp:Enabled` (default false), `Mcp:ListenUri` (default `http://0.0.0.0:2525`), `Mcp:ApiKey` (secret) — the Odysseus endpoint. `Mcp:ApiKey` is required when enabled; the endpoint stays down without it.
- *(planned, §3.12 / FR-92–FR-95, FR-99, FR-114)* `Llm:ApiKey` (secret; no key = no LLM features), `Llm:Model` (default `mistral-small-latest`), `Llm:Persona` (persona seed text; defaults to the shipped persona), `Llm:PresenceUpdates:Enabled` (default false), `Llm:BioUpdates:Enabled` (default false), `Llm:PresenceUpdates:IntervalMinutes` (default 60), `Llm:DailyRequestBudget` (default 500; `0` disables the LLM feature set like an absent key, FR-103).
- *(§3.13 / FR-105–FR-106, FR-117–FR-118)* `Watchdog:CheckIntervalMinutes` (default 5), `Watchdog:GatewayDownAlertMinutes` (default 10), `Watchdog:ReAlertMinutes` (default 60) — proactive alerts to the configured bot owner; log-only when no owner is configured. `Notifications:ChannelId` (optional; the default operator notification channel, overridable at runtime via `!notifychannel`), `Notifications:MaxPostsPerMinute` (default 5). In Compose these arrive as `WATCHDOG_CHECK_INTERVAL_MINUTES`, `WATCHDOG_GATEWAY_DOWN_ALERT_MINUTES`, `WATCHDOG_RE_ALERT_MINUTES`, and `NOTIFY_CHANNEL_ID` from `.env`.
- *(§3.14 / FR-107–FR-109)* `Reminders:MinRecurrenceMinutes` (default 10), `Reminders:MaxRecurring` (default 5; `0` disables the recurring form) — the recurring-reminder limits. Absent or invalid entries fall back to the defaults (§3.4 treatment).
- *(§3.15 / FR-110)* `SlashCommands:Enabled` (default false) — the slash surface (registration + interaction handling); the eligible command set — administrative/configuration and user-scoped commands — is fixed per command in code, not via this key.
- *(§5.2 / FR-81–FR-82)* Backup scheduling is compose-service environment, not bot configuration: `BACKUP_INTERVAL` (default 24 h), `BACKUP_RETENTION` (default 7), `BACKUP_TEST_INTERVAL` (default 30 days), `BACKUP_RCLONE_REMOTE` plus offsite credentials — all from the gitignored `.env`. The bot itself reads only `Backups:Directory` (the mounted shared volume; `/app/backups` in Compose, `backups` locally) for the in-bot backup commands.

## 5. Deployment

The bot is containerized via `HambotV2/Dockerfile` (multi-stage .NET 10 build → `dotnet publish` → runtime-only final image). The build context is the repository root, and the image is secret-free: `appsecrets.json` is excluded via `.dockerignore`, secrets are supplied as environment variables (`Secrets__BotToken`, `ConnectionStrings__DefaultConnection`).

The recommended deployment is `docker-compose.yml` at the repository root, which brings up three services: PostgreSQL (`postgres:17` with a healthcheck and persistent `db-data` volume), the bot (waiting for the database to become healthy), and the backup sidecar (§5.2, built from `scripts/backup/` and sharing the persistent `backups` volume with the bot). Compose variables come from a gitignored `.env` file (structure in `.env.example`):

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

### 5.1 CI/CD pipeline — Jenkins auto-deploy

The `Jenkinsfile` is a declarative pipeline with three stages; a pushed version tag (`v<major.minor.patch>`, C-5) that builds warning-free and passes all tests is deployed to the server automatically, with no manual steps (NFR-6). Ordinary pushes (including `master`) are built and tested but never deployed:

1. **Build** — `dotnet build` in Release with nullable warnings as errors.
2. **Test** — `dotnet test` (TRX results archived).
3. **Deploy** — gated on version tags only (ordinary pushes, feature branches, and PR builds stop after Test; a failed build or test run never reaches this stage). The Jenkins agent SSHes into the host — Jenkins itself runs in a container on the same server — where the deploy directory is a git clone of the same origin. The stage checks that clone out at exactly the commit CI built (`git fetch origin && git checkout --force $GIT_COMMIT`), then runs the repository's own `scripts/deploy.sh` with the commit SHA, build number, and build URL. The deployed bot reports the tag + commit of exactly that revision (§3.9): the agent-side build reads it from the workspace's git for free, and `deploy.sh` passes the same pieces to the container build.

`scripts/deploy.sh` (runs on the server, in the deploy directory) provides the deploy hygiene:

- It snapshots the currently running bot image as a rollback point, then builds the new image **before** touching any container — a failed build leaves the previously running stack untouched.
- `docker compose up -d --no-build` then replaces only changed containers. Secrets come exclusively from the server's gitignored `.env` next to `docker-compose.yml`; nothing sensitive appears in the `Jenkinsfile`, the job configuration, or the build log (NFR-3/NFR-4).
- Post-deploy verification: the script waits for the `db` healthcheck and polls the bot container logs for the known startup lines ("Database migrations applied successfully", "Discord gateway connection established.", and a "Bot version:" line matching the deployed tag + commit, §3.9). If they do not appear in time, it restores the previously running image and exits non-zero, so a failed deploy leaves the old version serving and the Jenkins job goes red instead of shipping a broken bot.
- On success it prunes dangling images from the rebuild and writes a gitignored `.deploy-info` file (commit, version, Jenkins build number and URL, timestamp) next to the checkout so the running version is traceable from the Jenkins UI and from the server.

One-time server preparation (not part of the pipeline):

```bash
# deploy user with Docker access (docker group membership is effectively root-equivalent
# on the host — acceptable for this single-server deployment; harden with rootless
# Docker if that ever changes)
sudo useradd -m hambot-deploy && sudo usermod -aG docker hambot-deploy
sudo mkdir -p /opt/hambotv2 && sudo chown hambot-deploy /opt/hambotv2
sudo -u hambot-deploy git clone <origin-url> /opt/hambotv2   # deploy key if the repo is private
cd /opt/hambotv2 && sudo -u hambot-deploy cp .env.example .env  # fill in; the pipeline never touches this file

# Jenkins side: generate an SSH key pair for the deploy user (no passphrase), add the
# public key to ~hambot-deploy/.ssh/authorized_keys, and store the private key in
# Jenkins as an "SSH Username with private key" credential (username hambot-deploy).
# The Jenkins container must reach the host's sshd: use the host's IP, the docker
# bridge gateway (172.17.0.1), or add extra_hosts ["host.docker.internal:host-gateway"].
# Jenkins needs the "SSH Agent" plugin; set DEPLOY_HOST/DEPLOY_USER/DEPLOY_DIR/
# DEPLOY_SSH_CREDENTIAL in the Jenkinsfile environment block accordingly.
```

### 5.2 Database backups — scheduled dumps, local retention, offsite push *(FR-81–FR-85 / NFR-19)*

**Backup sidecar.** Backups run as a dedicated `backup` service in `docker-compose.yml`, not inside the bot: the database is backed up even while the bot container is stopped, restarting, or failing (FR-84), and the bot's runtime-only image stays as it is. The service is built from `scripts/backup/Dockerfile` — a `postgres:17-alpine` base (for `pg_dump`/`pg_restore`) plus `rclone` (a single binary covering S3-compatible remotes: AWS S3, Backblaze B2, Cloudflare R2, …).

**Backup loop.** `scripts/backup/run-backup.sh` runs an endless loop, configured entirely via environment (§4). A pass runs immediately on start (a fresh deployment produces its first backup right away) and then sleeps between passes:

1. `pg_dump -Fc` (compressed custom format — restores cleanly with `pg_restore`, including into a fresh database) against the compose `db` service, writing a timestamped `hambotv2-<YYYYMMDD-HHMMSS>.dump` into the persistent `backups` volume (FR-81).
2. Validate the fresh dump immediately (FR-100): a restore-catalog listing of the file (`pg_restore --list`) plus a minimum size floor — a dump that fails either check is logged as a failed backup, removed, and counts for nothing downstream.
3. Prune local dumps beyond `BACKUP_RETENTION` (default 7), keeping the most recent *valid* dumps — only after the new dump exists and is validated, so a failed dump never deletes good backups (FR-81/FR-100).
4. Push the new dump offsite via `rclone copy` to the configured remote, if one is configured (`BACKUP_RCLONE_REMOTE` + credentials); without a remote the step is skipped, and a failed push is logged and otherwise harmless — the local dump, its retention, and the bot are unaffected (FR-82).
5. Write the status file (`backups/.backup-status`: last successful backup, last validation result, last restore-test result) — the channel `!health` and the watchdog consume (§3.13). Then sleep `BACKUP_INTERVAL` (default 24 h) until the next pass.

**Automated restore test (FR-101).** On a configurable schedule (`BACKUP_TEST_INTERVAL`, default monthly), the loop proves restorability without touching production state: create a scratch database (`hambotv2_verify`), `pg_restore` the newest valid dump into it, verify that every table the dump's own restore catalog lists exists in the scratch database, drop the scratch database, and record the outcome in the status file — a failed test is logged and raises a watchdog alert (§3.13). Deriving the expectation from the dump's catalog (rather than a hardcoded table list) keeps the check in sync with migrations without code changes and avoids racing the bot's startup migrations — a first-deploy sidecar may dump a pre-migration database before the bot's `Database.Migrate()` has run, and that dump is still a faithful, restorable backup of what the database was at that moment. The operator restore drill (FR-83/NFR-19) remains the authoritative proof of the full deployment path.

All credentials (PostgreSQL password, offsite storage credentials) come from the server's gitignored `.env` — nothing sensitive is committed (FR-85, NFR-3).

**Restore (FR-83).** `scripts/restore-backup.sh <dump-file>` is the inverse drill: stop the bot container → drop and recreate the database → `pg_restore` the dump → start the stack → poll the bot logs for the known startup lines (the same verification `deploy.sh` performs, §5.1). The script and the procedure are documented; proving it once against a scratch database is a roadmap operator action (NFR-19).

**In-bot backup management (FR-96–FR-98 / NFR-22).** Recovery should not require host access: the bot owner can list, create, and restore backups entirely from chat. Two deployment changes make that possible: the bot's runtime image installs `postgresql-client` (pg_dump/pg_restore), and the compose `bot` service mounts the shared `backups` volume — so on-demand dumps land in the same place as the scheduled ones and the commands see both. The bot runs as a non-root user while the sidecar runs as root, so the sidecar opens the shared directory itself on every start (`chmod 1777`, sticky like `/tmp`: anyone may create files, only their owner may delete them) — otherwise a volume created root-owned would deny the bot's pg_dump its output file. The pg tools receive their connection as a `postgresql://` URI converted from the Npgsql connection string (`ProcessBackupManager.BuildPostgresUri`), since libpq does not understand Npgsql keyword syntax; pg-tool failures (exit code plus stderr) are logged at the manager before the commands reply generically, so "check the bot logs" actually leads somewhere.

- **Process boundary — `Backups/IBackupManager`**: `CreateBackupAsync()` (timestamped `pg_dump -Fc` into the volume, returns name + size), `ListBackups()` (the dump files, newest first), `RestoreBackupAsync(name)` (`pg_restore --clean --if-exists` into the live database). Constructor-injected into the commands so unit tests fake it (NFR-22) and the commands stay pure orchestration.
- **Commands** — `Commands/BackupCommands/` (`BackupCommand`, `BackupsCommand`, `RestoreBackupCommand`), registered in the registration chain (§3.2); all tier 4 by default (global operations), DM-usable, with help text: `!backup` → on-demand dump, reply with name + size; `!backups` → list newest first capped at 10, friendly empty message; `!restorebackup <name>` → destructive, therefore two-step: the first invocation warns and demands `!restorebackup <name> confirm`, only the confirmed invocation runs the restore.
- **Maintenance mode (FR-98)**: a singleton flag set around the restore, consulted by the command pipeline (other commands get a maintenance notice), the MCP tool surface (§3.11 — DB-touching tools defer), and the scheduled delivery engine (§3.8 — delivery passes are skipped entirely, so a restore can never exhaust a reminder's retry budget and mark it failed). Pooled database connections are cleared before the restore (`NpgsqlConnection.ClearAllPools`), and afterwards the guild-settings cache is invalidated (identifier overrides may differ in the restored data).
- **Division of labor**: the command path is the convenient revert for a *healthy* bot; the scripted procedure (FR-83) remains the authoritative recovery path for a broken bot or a broken database, since it runs from outside the stack.

## 6. Testing

- `BotTests/` contains MSTest + Moq tests: `TestCommandInfrastructure.cs`, `TestCommands.cs`, `TestPermissions.cs`, `TestCommandHandler.cs`, `TestConfigurationValidator.cs`, `TestPermissionCommands.cs`, `TestHelp.cs`, `TestCommandArguments.cs`, `TestGuildSettings.cs`, `TestCooldowns.cs`, `TestQuotes.cs`, `TestCustomResponses.cs`, `TestReminders.cs`, `TestVersion.cs`, `TestBackups.cs`, `TestWatchdog.cs`, `TestPresence.cs`, `TestProfileImages.cs`, plus the shared `DiscordTestDoubles.cs`.
- Tests run without a reachable database or Discord token: Moq doubles the Discord message surface, and command/permission tests run against the EF Core **InMemory** provider through the `IDatabaseContext` abstraction (isolated database per test).
- Coverage focuses on: command registration contract (lowercase names, help text, valid default tiers), robust command parsing (bare identifier, whitespace variations, custom identifiers), command lookup, the argument-parsing helper (tokenization, typed accessors, mention parsing), the permission tier scale with tier overrides and escalation guards, per-guild scoping (a user's or command's tier in one guild has no effect in another), the DM scope (owner-tier execution, built-in default command tiers, no database writes, guild-state command declines), permission-management command behavior (mentions, tier validation, upserts, guard rejections), `!help` listing/detail/non-revealing denials (also in DMs), guild settings (identifier override/fallback, cache refresh, `!settings` set/reset/validation), command cooldowns (`TestCooldowns.cs`, via `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider`: throttling inside the window, expiry after advancing time, independence per user/command/guild, per-tier global windows with the 3-second default, guild override beats the global default with `reset` falling back, `0` disabling a tier, the tier-4 exclusion, the DM sentinel key, the pipeline throttle reply and `Throttled` audit event, and the `!settings cooldown` escalation guard), the `CommandHandler` failure paths (throwing command, throwing permission check, denied permissions, unknown command, bare-identifier ignore, `DatabaseContext` disposal), the structured command audit event (exactly one Information event per message with the expected `CommandOutcome`, no message content in the audit line), startup configuration validation, and the quotes feature (`TestQuotes.cs`, NFR-14 — add/get/remove lifecycle with sequential numbering including top-number reuse after removing the highest quote, per-guild isolation, random fetch on a populated guild and the empty-guild message, fetch-by-number including unknown and beyond-int-range numbers, add-by-text including an out-of-long-range numeric argument, search behavior with its case-insensitive match, ordering and 10-result cap, text validation at the 2000-char boundary and the reply trimming of over-limit fetches and search results, permission gating with runtime tier adjustment, and DM declines). The legacy import (`!importquotes`, FR-72–FR-75) is covered by the same file: import with continued numbering, file order, and legacy dates, entries at the 2000-char boundary, every failure path (missing attachment, download failure, malformed JSON, empty list, empty entry, over-long entry) replying in-channel and storing nothing, the tier-4 default gate (guild owner denied, configured bot owner allowed), and the DM decline; a fake `IAttachmentContentDownloader` stands in for the HTTP download. The backup and watchdog features have their own files: `TestBackups.cs` (NFR-22 — against a fake `IBackupManager`: on-demand trigger and reply, list behavior and its 10-entry cap, the two-step restore confirmation and its negative cases, maintenance-mode gating of other commands and deferred reminder deliveries without marking them failed, the tier-4 gate, DM availability, failure replies, and the Npgsql→libpq connection-URI conversion) and `TestWatchdog.cs` (NFR-23 — with fakes and `FakeTimeProvider`: each condition's detection including the alert/re-alert/recovery cadence, delivery via a mocked sender to both the owner DM and the notification channel, the no-owner log fallback, the fresh-vs-stale never-run restore test, `!health` fields and tier gate, notification posting with the rate limit and identical-message collapse, and `!notifychannel` show/set/clear with persistence).
- Build and run:

  ```bash
  dotnet build HambotV2.sln
  dotnet test HambotV2.sln
  ```

- The build is warning-free and CI treats warnings as errors; new code must not add any.
- **Feature coverage** (NFR-15): `BotTests/TestCustomResponses.cs` covers the scenarios the requirements enumerate — name/content validation, built-in collision rejection, create-vs-update, removal, per-guild isolation, dispatch order and gating (through a full `CommandHandler` pipeline over the EF InMemory provider with the real permission and cooldown services), attachment rejection, and DM behavior.
- **Feature coverage** (NFR-16): `BotTests/TestReminders.cs` — Moq doubles for the Discord surface, the EF InMemory provider through `IDatabaseContext`, and `FakeTimeProvider` for reminder scheduling — covers duration parsing across all forms (units, long/compact/spaced forms, sums, invalid and non-positive input, multi-year durations), message validation, confirmation, per-user isolation and caps, list/cancel behavior, and the delivery loop: due-vs-not-due as time advances (including re-scheduling when an earlier reminder is added and waiting while nothing is pending), restart recovery (a persisted due reminder is delivered when the service starts), and the bounded-failure path (exactly three attempts, then marked failed, other reminders still delivered).

## 7. Conventions

- C# with nullable reference types and implicit usings enabled; `LangVersion` latest in the test project.
- Bot-internal types are `internal` (tests rely on `InternalsVisibleTo`, which also grants `DynamicProxyGenAssembly2` for Moq); new command implementations should be `internal` too.
- Command names must be lowercase.
- DI lifetimes: `CommandService` singleton; `CommandHandler`, `PermissionsService`, `DatabaseContext` transient; handler resolved per message and disposed after use.

## 8. Known Design Considerations / Current Limitations

These are observations of the current implementation:

- **CI/CD**: a declarative `Jenkinsfile` at the repository root builds the solution, runs the tests (with nullable warnings treated as errors), and — for version tags (`v<major.minor.patch>`, C-5) only — deploys the exact tested commit to the server via SSH and `scripts/deploy.sh` (§5.1); ordinary pushes (including `master`) are built and tested but never deployed. The Jenkins job must be configured as "Pipeline script from SCM" pointing at it.

