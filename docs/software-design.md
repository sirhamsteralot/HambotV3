# Software Design

This document describes the design of HambotV2. It is the overview for contributors and the baseline for changes. Unless a section is marked **(planned)**, it describes the system as implemented today.

Three features from [`software-requirements.md`](software-requirements.md) were specified ahead of implementation: **quotes** (FR-42–FR-50, plus the legacy import FR-72–FR-75, NFR-14), **custom command responses** (FR-51–FR-60, NFR-15), and **reminders** (FR-61–FR-71, NFR-16), designed as §3.6, §3.7, and §3.8. Quotes and custom command responses are implemented (§3.6–§3.7 describe the system as it is); reminders are still **(planned)** — that is the design the implementation must follow. The [roadmap](roadmap.md) sequences the work; when a feature lands, remove its *planned* markers and fold any deviations back here so this document keeps describing the system as it actually is.

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

A second hosted service is planned alongside the Discord client: `ReminderDeliveryService` (§3.8), a `BackgroundService` that delivers due reminders as direct messages.

### 2.1 Composition root — `HambotV2/Program.cs`

- Registers `appsecrets.json` as an additional configuration source.
- Replaces the default logging providers with Serilog (`builder.Logging.ClearProviders()` + `AddSerilog`), configured via `ReadFrom.Configuration` from the `Serilog` configuration section, so sinks and levels come from `appsettings.json` / environment variables (see §4).
- DI registrations:

| Service | Interface | Lifetime |
|---|---|---|
| `DiscordClient` | hosted service | singleton (hosted) |
| `CommandService` | `ICommandService` | singleton |
| `GuildSettingsService` | `IGuildSettingsService` | singleton (caches per-guild command identifiers) |
| `CooldownService` | `ICooldownService` | singleton (shares the in-memory cooldown timestamps across messages) |
| `TimeProvider` | — | singleton (`TimeProvider.System`; injectable so tests advance time deterministically) |
| `CommandHandler` | `ICommandHandler` | transient (resolved per message, disposed after use) |
| `PermissionsService` | `IPermissionsService` | transient |
| `DatabaseContext` | concrete `DbContext` and `IDatabaseContext` | transient (both service and options lifetime) |
| `ReminderScheduler` *(planned, §3.8)* | `IReminderScheduler` | singleton — wake-up coordination between the reminder commands and the delivery service |
| `ReminderDeliveryService` *(planned, §3.8)* | hosted service | singleton (hosted) |

## 3. Component Design

### 3.1 Discord connection — `Connections/DiscordClient.cs`

A `BackgroundService` that:

1. Creates a `DiscordSocketClient` with only the gateway intents the bot uses: `Guilds | GuildMessages | DirectMessages | MessageContent` (explicit intents instead of `AllUnprivileged` avoid Discord.Net warnings for unused intents).
2. Logs in with the token from configuration key `Secrets:BotToken` and starts the gateway.
3. Subscribes to `MessageReceived`. Messages from bots and webhooks (including the bot itself) are ignored — otherwise the bot would re-process its own replies that start with the identifier (e.g. the `!help` detail output). Pre-filter (FR-26): the *effective command identifier of the message's guild* — resolved from `IGuildSettingsService` (per-guild override, global default fallback) — decides which messages are command invocations; direct messages use the global default, since DM commands run in the DM scope (see 3.2). Matching messages are forwarded to a freshly resolved `ICommandHandler` (resolved from `IServiceProvider`, disposed after handling).
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

**`ICommandContext` / `CommandContext`** — a thin wrapper exposing the per-message `IDatabaseContext` to command implementations, keeping commands decoupled from DI. It also carries a `TierOverrides` record (see 3.3) — the message's guild owner plus the configured bot owner id — and `GetUserEffectiveTier(userId)` computes a user's effective tier so commands share the exact semantics the permission check uses (in the DM scope it returns owner tier without a database read). Per-guild scoping: the context additionally carries `GuildId` (the invoking guild; **null in direct messages**, exposed as `IsDirectMessage`), `CommandIdentifier` (the guild's effective identifier, or the global default in DMs; used by `!help` for display), and `GuildSettings` (the `IGuildSettingsService`, used by `!settings` to write guild settings with cache invalidation).

**`CommandHandler`** (`ICommandHandler`) — per-message orchestrator, transient and disposable:

1. **DM scope (FR-25)**: commands also run in direct messages. A null guild id marks the DM scope: the user acts at **owner tier (3)** — the configured bot owner at tier 4 — and commands require their **built-in default tiers**; no per-guild state is read or written, so DMs need no database rows and permissions cannot be adjusted from a DM. Commands that manage guild state (`!userpermission`, `!commandpermission`, `!settings`, the quote commands, the response-management commands) decline in DMs with "…only available in servers". The `DirectMessages` gateway intent stays enabled so DM commands are received at all.
2. Resolve the effective command identifier via `IGuildSettingsService` — the guild's identifier, or the global default in DMs — then the command via `ICommandService.TryGetCommand(content, identifier)`; identifier-only messages (a bare `!`) are ignored silently, unknown commands get "The requested command is unavailable". The unknown-command branch first consults the invoking guild's custom responses (§3.7, FR-10/FR-54) — through the same permission and cooldown checks — and falls back to the "unavailable" reply only on a miss; in direct messages the reply stays as-is (FR-59).
3. Check permissions via `IPermissionsService.CheckUserPermissions`, passing the guild id (null = DM scope) and a `TierOverrides` built from the guild owner (null in DMs) and the configured bot owner id; if insufficient, reply "You are not permitted to use that command". The check returns a `PermissionCheckResult` carrying the verdict *and* the user's effective tier, so the cooldown check (next step) resolves its window without repeating the database lookup — and the tier it uses is the guarded one, so the window cannot be bypassed via permission lookups.
4. Check the command cooldown via `ICooldownService.TryBeginInvocation` (see the cooldown component below); a throttled invocation is answered with "You're using commands too quickly — please wait N more seconds." and never executes the command.
5. Execute the command with a `CommandContext` wrapping the per-message `IDatabaseContext`, the guild id (nullable), the effective identifier, the settings service, and the permission context.

The whole pipeline is wrapped in a try/catch: on any failure (throwing command, database outage during the permission check, ...) the exception is logged with the command name and message content, and the user gets a generic in-channel error message ("An unexpected error occurred...") that never contains exception details. Each failing message is handled independently — a single failure never takes the bot down, and subsequent messages keep being processed. On disposal the handler also disposes its per-message `DatabaseContext`, so connections are released after every message regardless of success or failure.

**Command audit event** (NFR-11) — `HandleCommand` emits exactly one structured Information-level event per handled message, in a `finally` block so every exit path is covered (including throws). The event carries named fields for filterability: `CommandName` (resolved name, null when unknown/identifier-only), `Outcome` (the `CommandOutcome` enum in `Commands/CommandOutcome.cs`: `Executed`, `DeniedPermission`, `UnknownCommand`, `Ignored` for bare-identifier messages, `Throttled` for cooldown rejections, `Failed`), `GuildId` (null in DMs), `ChannelId`, `UserId`, and `DurationMs` (handler wall time). Message content never appears in the audit event — only the Error-level failure log contains it — keeping routine usage logs free of user text. This single event replaces the former per-branch log lines, making command usage traceable from logs without debugging.

**`CommandService`** (`ICommandService`) — singleton command registry:

- Holds a `Dictionary<string, ICommand>` keyed by command name.
- Lazily populated on first lookup (`CommandsRegistered` flag) by calling `RegisterAllCommands.RegisterCommands`.
- `RegisterCommand` is the single chokepoint for the command contract: it rejects non-lowercase or empty names, missing `Description`/`Usage`, and invalid `DefaultPermissionTier` values, so a command without help text can never enter the registry.
- `TryGetCommand(content, identifier)` parses via the shared `CommandParsing` helper. Parsing rules: the command identifier — the *guild's effective identifier*, passed by the handler — is matched case-sensitively as an exact prefix of the trimmed message; any run of whitespace separates tokens; the command name is the first token after the identifier, lowercased. Bare identifier-only messages (e.g. `!` or `!   `) resolve to no command.
- `IsIdentifierOnlyMessage(content, identifier)` lets the handler distinguish "no command name given" (ignored silently) from "unknown command" (answered).

**Command registration** — a small static registration chain:

- `Commands/RegisterAllCommands.cs` → delegates to `Commands/SystemCommands/RegisterSystemCommands.cs`, `Commands/QuoteCommands/RegisterQuoteCommands.cs`, and `Commands/ResponseCommands/RegisterResponseCommands.cs`. The remaining planned feature group (§3.8) adds its own `RegisterReminderCommands` to the same chain.
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

EF Core `DbContext` against PostgreSQL with five nested entity classes implemented today, all scoped per guild (FR-20/FR-24), plus one planned entity for the roadmap feature (§3.8):

| Entity | Key | Fields |
|---|---|---|
| `User` | `(GuildId ulong, DiscordId ulong)` | `PermissionTier` (int) |
| `CommandMetaData` | `(GuildId ulong, CommandName string)` | `PermissionTier` (int) |
| `GuildSetting` | `GuildId (ulong)` | `CommandIdentifier` (string?, null = global default applies, FR-26) and the per-tier cooldown overrides `CooldownSecondsTier0`–`CooldownSecondsTier3` (int?, used by the cooldown service; null = inherit the global default). The tier→column mapping lives on the entity (`GetCooldownOverride`/`SetCooldownOverride`) as the single source of truth for both the cooldown service and `!settings`. |
| `Quote` (§3.6, FR-42) | `(GuildId ulong, Number int)` | `Text` (1–2000 chars), `AddedByDiscordId` (ulong), `CreatedAt`. |
| `CustomResponse` (§3.7, FR-51) | `(GuildId ulong, Name string)` | `Content` (1–1000 chars), `LastModifiedByDiscordId` (ulong), `CreatedAt`, `UpdatedAt`. |
| `Reminder` *(planned, §3.8, FR-61)* | `(OwnerDiscordId ulong, Number int)` | `Message` (1–500 chars), `DueAtUtc` (DateTimeOffset), `GuildId` (ulong?, null = created in a DM), `CreatedAt`, `FailedAtUtc` (DateTimeOffset?, null = pending). |

Composite keys are configured in `OnModelCreating`. EF Core migrations live in `HambotV2/Migrations/`: `InitialCreate`, then `PerGuildScoping` (adds the `GuildId` dimension and the `GuildSettings` table), then `AddQuotes` (§3.6), then `AddCustomResponses` (§3.7). Pre-existing rows keep the sentinel `GuildId = 0` there — it matches no real guild id, so legacy tiers are inert; admins re-grant tiers per guild (guild owners already bootstrap at owner tier, FR-34). Regenerate with `dotnet ef migrations add <Name>` from the `HambotV2/` project directory; the `DesignTimeDatabaseContextFactory` lets the EF tools scaffold migrations without running `Program.Main` (which would attempt a real migration). The remaining planned feature adds exactly one migration of its own — `AddReminders` (§3.8) — covered by the automatic startup migration (FR-30), so no manual step is introduced.

Migrations are applied automatically on startup: `Program.Main` runs `Database.Migrate()` before the Discord gateway starts. If the database is unreachable, a critical error is logged (naming the connection configuration) and the host exits with a non-zero code instead of starting a half-functional bot. No manual migration step is required.

There is also an `Connections/IDatabaseContext.cs` abstraction exposing the `DbSet`s and `SaveChangesAsync`. The planned entity (§3.4 table) is exposed on it the same way, keeping command and service code testable against the EF InMemory provider. The whole command pipeline (`CommandHandler`, `CommandContext`, `PermissionsService`) depends on `IDatabaseContext`, and `Program.cs` maps it onto the registered transient `DatabaseContext`. This is what lets the command tests run against the EF InMemory provider instead of PostgreSQL.

### 3.5 Commands currently implemented

All in `Commands/SystemCommands/`, registered by `RegisterSystemCommands`:

| Command | Name | Behavior |
|---|---|---|
| `HelloWorld` | `!hello` | Replies "Hello World". |
| `HelpCommand` | `!help [commandName]` | Lists the commands available to the invoking user (required tier ≤ their effective tier), one line per command: identifier + name — description. With an argument: detailed help (description, usage, required tier) for that command — only if the user can run it; unknown and insufficient-tier names get the same generic "The requested command is unavailable" reply, so the existence of higher-tier commands is never revealed (FR-40). |
| `CommandPermission` | `!commandpermission <name> [tier]` | With 1 arg: prints the command's required tier (database override, or a hint that the built-in default applies). With 2 args: sets/creates the command's tier, guarded by the escalation rules in 3.3. Admin-only by default. Acts on the invoking guild only; declines in direct messages (FR-25). |
| `UserPermission` | `!userpermission <user> [tier]` | With 1 arg: prints the user's effective tier (with a "guild owner" / "bot owner" note where applicable). With 2 args: sets/creates the user's tier, guarded by the escalation rules in 3.3. `<user>` accepts raw ids and `@mentions`. Admin-only by default. Acts on the invoking guild only; declines in direct messages (FR-25). |
| `SettingsCommand` | `!settings [commandidentifier <value>\|commandidentifier reset\|cooldown <tier> <seconds>\|cooldown <tier> reset]` | Manages the invoking guild's bot settings (FR-26). No args: shows the effective command identifier (server override or global default) and the server's cooldown overrides. `commandidentifier <value>` sets the guild's override (non-empty, ≤ 5 chars; cached so it applies to the next message), `commandidentifier reset` clears it back to the global default. `cooldown <tier> <seconds>` sets the guild's per-tier cooldown override (tiers 0–3 only; seconds ≥ 0, where 0 disables throttling for that tier), `cooldown <tier> reset` clears it back to the global default; the escalation guard mirrors FR-36 — an invoker may only configure tiers strictly below their own, so nobody can loosen the throttle that governs their own level, while the bot owner (tier 4, never bound by a guild override) may configure any tier. Admin-only by default; the bot owner can manage any guild by running the command there. Declines in direct messages (FR-25). |

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

### 3.8 Reminders — user-scoped timed reminders *(planned, FR-61–FR-71 / NFR-16)*

**Entity & persistence.** `DatabaseContext.Reminder` with composite key `(OwnerDiscordId, Number)`: `Message` (1–500 chars), `DueAtUtc` (always UTC), `GuildId` (null = created in a direct message), `CreatedAt`, and `FailedAtUtc` (null = pending). Reminders are user-scoped, not guild-scoped: the key carries the owner, so one user's reminders follow them across guilds and DMs (FR-61). Migration: `AddReminders`.

**Sequential per-user numbering.** As with quotes (§3.6): `MAX(Number) + 1` within the owner at creation — the number increases by one with each reminder added. `!reminders` shows these numbers, `!cancelreminder` targets them; cancelling never renumbers the remaining reminders.

**Duration parsing** — `Commands/ReminderCommands/ReminderDurationParser.cs`, a pure, unit-testable helper with no Discord or database surface (NFR-13 spirit: one shared parser instead of per-command parsing):

- Leading number+unit tokens: units `s`/`m`/`h`/`d`/`w` plus long forms (`seconds` … `weeks`), compact (`10d 5h`) and spaced (`10 d 5 h`) forms both valid, multiple tokens summed (FR-62).
- The total must be positive; there is no minimum or maximum — durations of years are accepted (FR-63). Unparsable or non-positive input → in-channel error with a usage hint, and nothing is stored.
- The reminder message is the remainder after the duration tokens: non-empty, capped at 500 chars.

**Commands** — `Commands/ReminderCommands/`, registered by `RegisterReminderCommands`; all tier 0 and — being user-scoped — fully usable in direct messages (FR-71). Every query filters by `OwnerDiscordId`, so users can never see or cancel another user's reminders (FR-69).

| Command | Behavior |
|---|---|
| `remindme <duration> <message...>` | Persists the reminder (due = now + duration, UTC; guild id of creation, null in a DM) and confirms in-channel, echoing message and due time (FR-64). Enforces the per-user cap of 10 pending reminders — at cap, an in-channel error and nothing stored (FR-70). After saving, notifies the scheduler so a newly earliest reminder re-schedules delivery. |
| `reminders` | Lists the invoking user's pending reminders (number, due time, message), soonest first, capped at 10 entries (FR-68). |
| `cancelreminder <number>` | Cancels one of the invoking user's own pending reminders and confirms; unknown or foreign numbers → in-channel error (FR-69). Notifies the scheduler after deleting. |

**Delivery — `Reminders/ReminderDeliveryService.cs` + `Reminders/ReminderScheduler.cs` (FR-65–FR-67).** A second hosted `BackgroundService` (§2, §2.1) running beside the Discord client:

- All time comes from the injected `TimeProvider` (tests advance it via `FakeTimeProvider`); all database work goes through `IDatabaseContext` resolved from fresh `IServiceScopeFactory` scopes — the service never holds a `DbContext`.
- The singleton `IReminderScheduler` owns a wake-up signal (a `TaskCompletionSource`). The delivery loop queries the earliest pending due time, sleeps until `min(earliest due, now + 10 s)` — the bounded fallback poll covers clock drift and missed signals — then delivers every pending reminder with `DueAtUtc <= now` and repeats.
- `remindme`/`cancelreminder` set the signal after saving: whenever a newly created (or removed) reminder becomes the earliest pending one, the loop wakes and re-schedules instead of sleeping to the old horizon. Second-accurate wake-ups come from computing the delay against `TimeProvider`, not from polling faster (FR-65).
- Delivery sends the reminder message as a direct message to the owner; delivered rows are deleted from the pending set. Each delivery is isolated: a failure (e.g. closed DMs) is retried a bounded number of times with backoff, then the row is marked failed (`FailedAtUtc` set) and logged — it never blocks other reminders and never crashes the service (FR-67). Failed rows stay in the database for audit but leave the pending set.
- Restarts recover by construction (FR-66): the pending set is database state, so a reminder that became due while the bot was down is delivered on the service's first pass.

## 4. Configuration

- `appsettings.json` (committed, non-secret): `BotConfiguration:BotName`, `BotConfiguration:LogFormat`, `BotConfiguration:CommandIdentifier` (default `!`), `BotConfiguration:CooldownSecondsByTier` (global per-tier cooldown windows in seconds, e.g. `{ "0": 3, "1": 3, "2": 2, "3": 1 }`; missing entries default to 3 seconds and `0` disables throttling for that tier — in containers override per tier via `BotConfiguration__CooldownSecondsByTier__0` etc.; invalid or negative entries are treated as absent), plus the `Serilog` section.
- **Logging**: Serilog replaces the host's default logging providers (registered in `Program.cs` via `ClearProviders()` + `AddSerilog`, configured with `ReadFrom.Configuration`). The `Serilog` section defines the minimum level (with per-namespace overrides, e.g. `Microsoft`/`System` at Warning) and the sinks: Console and a daily-rolling File sink to `logs/hambot-.log` with 31 days retention (the `logs/` output is gitignored; in containers it lives in the container filesystem — mount a volume if retention matters). The level is adjustable at deploy time via `appsettings.json` or environment variables without a rebuild; `appsettings.Development.json` raises the default to Debug for local development. `BotConfiguration:LogFormat` is unrelated to the sinks: it remains the format string for bridging Discord.Net's `Discord.Log` events (§3.1). All bot code keeps using `ILogger<T>`; Serilog is an implementation detail of the composition root.
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

- `BotTests/` contains MSTest + Moq tests: `TestCommandInfrastructure.cs`, `TestCommands.cs`, `TestPermissions.cs`, `TestCommandHandler.cs`, `TestConfigurationValidator.cs`, `TestPermissionCommands.cs`, `TestHelp.cs`, `TestCommandArguments.cs`, `TestGuildSettings.cs`, `TestCooldowns.cs`, `TestQuotes.cs`, plus the shared `DiscordTestDoubles.cs`.
- Tests run without a reachable database or Discord token: Moq doubles the Discord message surface, and command/permission tests run against the EF Core **InMemory** provider through the `IDatabaseContext` abstraction (isolated database per test).
- Coverage focuses on: command registration contract (lowercase names, help text, valid default tiers), robust command parsing (bare identifier, whitespace variations, custom identifiers), command lookup, the argument-parsing helper (tokenization, typed accessors, mention parsing), the permission tier scale with tier overrides and escalation guards, per-guild scoping (a user's or command's tier in one guild has no effect in another), the DM scope (owner-tier execution, built-in default command tiers, no database writes, guild-state command declines), permission-management command behavior (mentions, tier validation, upserts, guard rejections), `!help` listing/detail/non-revealing denials (also in DMs), guild settings (identifier override/fallback, cache refresh, `!settings` set/reset/validation), command cooldowns (`TestCooldowns.cs`, via `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider`: throttling inside the window, expiry after advancing time, independence per user/command/guild, per-tier global windows with the 3-second default, guild override beats the global default with `reset` falling back, `0` disabling a tier, the tier-4 exclusion, the DM sentinel key, the pipeline throttle reply and `Throttled` audit event, and the `!settings cooldown` escalation guard), the `CommandHandler` failure paths (throwing command, throwing permission check, denied permissions, unknown command, bare-identifier ignore, `DatabaseContext` disposal), the structured command audit event (exactly one Information event per message with the expected `CommandOutcome`, no message content in the audit line), startup configuration validation, and the quotes feature (`TestQuotes.cs`, NFR-14 — add/get/remove lifecycle with sequential numbering including top-number reuse after removing the highest quote, per-guild isolation, random fetch on a populated guild and the empty-guild message, fetch-by-number including unknown and beyond-int-range numbers, add-by-text including an out-of-long-range numeric argument, search behavior with its case-insensitive match, ordering and 10-result cap, text validation at the 2000-char boundary and the reply trimming of over-limit fetches and search results, permission gating with runtime tier adjustment, and DM declines). The legacy import (`!importquotes`, FR-72–FR-75) is covered by the same file: import with continued numbering, file order, and legacy dates, entries at the 2000-char boundary, every failure path (missing attachment, download failure, malformed JSON, empty list, empty entry, over-long entry) replying in-channel and storing nothing, the tier-4 default gate (guild owner denied, configured bot owner allowed), and the DM decline; a fake `IAttachmentContentDownloader` stands in for the HTTP download.
- Build and run:

  ```bash
  dotnet build HambotV2.sln
  dotnet test HambotV2.sln
  ```

- The build is warning-free and CI treats warnings as errors; new code must not add any.
- **Feature coverage** (NFR-15): `BotTests/TestCustomResponses.cs` covers the scenarios the requirements enumerate — name/content validation, built-in collision rejection, create-vs-update, removal, per-guild isolation, dispatch order and gating (through a full `CommandHandler` pipeline over the EF InMemory provider with the real permission and cooldown services), attachment rejection, and DM behavior.
- **Planned coverage** (NFR-16): the remaining feature task (§3.8) adds `TestReminders.cs` — Moq doubles for the Discord surface, the EF InMemory provider through `IDatabaseContext`, and `FakeTimeProvider` for reminder scheduling, covering duration parsing across all forms, message validation, confirmation, per-user isolation and caps, list/cancel, due-vs-not-due delivery as time advances including re-scheduling, restart recovery, and the bounded-failure path.

## 7. Conventions

- C# with nullable reference types and implicit usings enabled; `LangVersion` latest in the test project.
- Bot-internal types are `internal` (tests rely on `InternalsVisibleTo`, which also grants `DynamicProxyGenAssembly2` for Moq); new command implementations should be `internal` too.
- Command names must be lowercase.
- DI lifetimes: `CommandService` singleton; `CommandHandler`, `PermissionsService`, `DatabaseContext` transient; handler resolved per message and disposed after use.

## 8. Known Design Considerations / Current Limitations

These are observations of the current implementation:

- **CI**: a declarative `Jenkinsfile` at the repository root builds the solution and runs the tests (with nullable warnings treated as errors); the Jenkins job must be configured as "Pipeline script from SCM" pointing at it.
- **Planned features**: reminders (FR-61–FR-71) are specified in the requirements and designed in §3.8 but not yet implemented; the [roadmap](roadmap.md) sequences the work.

