# Software Design

The design of HambotV2, as implemented. Sections marked **(planned)** describe roadmap work not yet built; everything else is as-built. Requirements are numbered in [`software-requirements.md`](software-requirements.md) (FR/NFR).

## 1. System Overview

Discord chat bot on .NET 10 (generic host), **Discord.Net 3.20.2** for the gateway, **PostgreSQL** via **EF Core 10 / Npgsql**, and a small custom command pipeline with per-guild tiered permissions.

| Project | Purpose |
|---|---|
| `HambotV2/` | The bot: hosted services, command pipeline, EF Core model and migrations. |
| `BotTests/` | MSTest + Moq unit tests (`InternalsVisibleTo`); run without a database or token. |

Key dependencies: Discord.Net, EF Core/Npgsql, Microsoft.Extensions.Hosting, Serilog (console + file sinks), MSTest + Moq (tests).

## 2. Runtime Architecture

```
                    ┌──────────────────────────────────────────────┐
                    │  Generic Host (Program.Main)                 │
                    │  - Configuration (appsettings + appsecrets)  │
                    │  - DI container / logging                    │
                    └───────────────┬──────────────────────────────┘
                                    │ hosts
                                    ▼
┌────────────────┐  message   ┌──────────────────┐
│ Discord gateway│ ──────────► │ DiscordClient    │ (BackgroundService)
│ (Discord.Net)  │             │ Connections/     │
└────────────────┘             └────────┬─────────┘
                                        │ messages starting with the guild's
                                        │ command identifier
                                        ▼
                               ┌──────────────────┐        ┌──────────────────┐
                               │ CommandHandler   │ ──────►│ PermissionsService│
                               │ (per message)    │        └────────┬─────────┘
                               └──────┬───────────┘                 │
                                      │ resolve + execute           ▼
                              ┌───────▼───────────┐        ┌──────────────────┐
                              │ CommandService    │        │ DatabaseContext  │
                              │ (singleton, lazy  │        │ (EF Core/Npgsql, │
                              │  command registry)│        │  transient)      │
                              └───────────────────┘        └──────────────────┘
```

### 2.1 Composition root — `HambotV2/Program.cs`

Registers `appsecrets.json` as a config source, replaces default logging with Serilog (`ReadFrom.Configuration`), and wires DI. Lifetimes: singletons — the shared `DiscordSocketClient` (created here with the gateway intents; used by `DiscordClient`, `ReminderDmSender`, and the quote-of-the-day sender, disposed by the container), `CommandService` (holds the `IScheduledDeliverySignal`), `GuildSettingsService` (caches per-guild identifiers and agent tiers), `VirtualCommandRunner` + `SlashCommandService` (§3.15), `QuoteSearchBacklog` + `ButtonPaginationService` (§3.19), `CooldownService`, `TimeProvider` (`TimeProvider.System`, injectable for tests), `ScheduledDeliverySignal`, `ReminderMessageSource`, `ReminderDmSender`, `DiscordQuoteOfTheDayChannelSender`, `DiscordPresenceGateway`, `PresenceService` (resolved eagerly in `Program.Main` so its Ready subscription exists before the gateway starts), `ImageContentDownloader`, plus the backup & stability, notification, health, and MCP singletons of their sections (§3.11, §3.13, §5.2). Transients — `CommandHandler` (per message, disposed after use), `PermissionsService`, `DatabaseContext` (also mapped onto `IDatabaseContext`). Hosted services: `DiscordClient`, `ScheduledMessageDeliveryService<ScheduledReminderMessage>`, `QuoteOfTheDayPublicationService`, `WatchdogService`, and the opt-in `McpServerService`.

## 3. Component Design

### 3.1 Discord connection — gateway message intake

`Connections/DiscordClient.cs` — a `BackgroundService` that logs in with `Secrets:BotToken` and subscribes to `MessageReceived`. Bot/webhook messages (including its own) are ignored. Messages starting with the guild's effective identifier (per-guild override via `IGuildSettingsService`, global default in DMs) are forwarded to a freshly resolved `ICommandHandler`. It also attaches the surface event handlers of the shared client: the pagination button handler unconditionally (§3.19) and, when `SlashCommands:Enabled`, the slash service's interaction handlers (§3.15). Gateway events are bridged to the host logger via `BotConfiguration:LogFormat`; connect/disconnect are logged; a keep-alive logs state every 5 minutes; shutdown logs out cleanly. The client requests only the intents the bot uses — `Guilds | GuildMessages | DirectMessages | MessageContent` — never `AllUnprivileged` (which warns about unused intents).

Tests: the identifier-prefix routing and ignore rules run through the pipeline suites (`TestCommandHandler.cs`, `TestCommandInfrastructure.cs`); the hosted service itself is thin Discord.Net glue.

### 3.2 Command pipeline — from message to command execution

The pipeline lives in `Commands/` and runs once per message:

**`ICommand`** — the command contract: `Name` (must be lowercase), `Description` (one line, shown in `!help`), `Usage` (argument summary), `DefaultPermissionTier`, `Category` (a `CommandCategory` enum value; presentation-only group for the help list), and `ExecuteCommand(IMessage, ICommandContext)`. Help text lives on the implementation so it cannot drift.

**`CommandContext`** — wraps the per-message `IDatabaseContext`, plus `GuildId` (null in DMs, `IsDirectMessage`), the guild's effective `CommandIdentifier`, a `TierOverrides` record (guild owner, configured bot owner, and — for virtual invocations — the operator agent), `GetUserEffectiveTier`, and the `IGuildSettingsService`.

**`CommandHandler`** — per-message orchestrator, in order:

1. **DM scope**: null guild id → the user acts at owner tier (bot owner at 4), commands require their built-in default tiers, and no guild state is read or written. Guild-state commands decline with "…only available in servers"; user-scoped commands (reminders) stay fully usable in DMs.
2. **Bot-ban check**: on a guild message, an active `BotBan` record drops the invocation silently — no reply at all (audit outcome `Banned`). The guild owner, bot owner, and operator-agent invocations are exempt; expired records are deleted on sight.
3. Resolve the identifier, then the command via `TryGetCommand`. Bare-identifier messages are ignored; unknown tokens first consult the guild's custom responses (§3.7, same permission and cooldown checks), then fall back to "The requested command is unavailable", with one appended "did you mean?" suggestion line when a close runnable candidate exists (§3.18). Maintenance mode (§5.2) declines everything except the restore confirmation, including custom responses.
4. Permission check via `IPermissionsService.CheckUserPermissions`, which returns the verdict *and* the user's effective tier (reused by the cooldown check). Insufficient → "You are not permitted to use that command".
5. Cooldown check via `ICooldownService.TryBeginInvocation` → "You're using commands too quickly — please wait N more seconds." without executing.
6. Execute with a `CommandContext`.

The whole pipeline is wrapped in a try/catch: failures are logged (command name + content) and answered with a generic in-channel error (never exception details), also posted to the notification channel when configured. One failure never stops later messages; the per-message `DatabaseContext` is disposed on all paths. Exactly one structured audit event per message (`finally`-covered): `CommandName`, `Outcome` (`Executed`, `DeniedPermission`, `UnknownCommand`, `Ignored`, `Throttled`, `Banned`, `Failed`), `GuildId`, `ChannelId`, `UserId`, `DurationMs` — no message content in routine events.

**`CommandService`** — singleton registry, `Dictionary<string, ICommand>`, lazily populated via `RegisterAllCommands.RegisterCommands`. `RegisterCommand` is the contract chokepoint (rejects non-lowercase names, missing help text, invalid tiers/categories). `TryGetCommand` matches the identifier as an exact prefix of the trimmed message (case-sensitive), the name as the first non-whitespace run after it, lowercased; `! quote` (space between) resolves to nothing. `IsIdentifierOnlyMessage` distinguishes "ignored" from "answered".

**Registration** — `RegisterAllCommands` delegates to `RegisterSystemCommands`, `RegisterQuoteCommands`, `RegisterResponseCommands`, `RegisterReminderCommands`; the chain receives the shared `IScheduledDeliverySignal` from `CommandService` so the reminder commands can wake the delivery loop.

**`Commands/CommandArguments.cs`** — the shared typed argument parser: `Parse` splits on whitespace runs and drops the first token; accessors `Count`, `Get`, `GetJoined` (free-form text), `GetRaw` (verbatim remainder, line breaks preserved), and `TryGetString`/`TryGetInt`/`TryGetDiscordUserId` (raw id or `<@id>` mention). No quoting; one token is one argument. New commands must use it instead of hand-parsing.

**`Commands/GuildSettingsService.cs`** — singleton resolving each guild's effective command identifier and operator-agent tier (override or default), cached in memory. The setters upsert the row *and* refresh the cache, so changes apply to the very next message. Never holds a `DbContext` (cache misses resolve a fresh scope).

**`Commands/CooldownService.cs`** — in-memory throttle keyed per **(guild, user, command)** (`ConcurrentDictionary` of timestamps, injected `TimeProvider`). The window comes from the guild's per-tier `GuildSetting` override, else `BotConfiguration:CooldownSecondsByTier` (default 3 s; `0`/negative disables). Tier 4 is never bound by a guild override (configuration-only role); DMs use the sentinel guild id 0. Runs after the permission check, before execution; denials are not throttled.

Tests: `TestCommandHandler.cs` (step order, outcomes, audit events, failure paths, the DM scope), `TestCommandInfrastructure.cs` (registry contract), `TestCommandArguments.cs` (the parser), `TestCooldowns.cs` (the throttle), `TestGuildSettings.cs` (identifier resolution).

### 3.3 Permissions — tiered access

`Commands/PermissionsService.cs` — the tier scale (`PermissionTier`): 0 everyone, 1 moderator, 2 admin, 3 owner (guild owner), 4 bot owner (`Permissions:BotOwnerDiscordId`; configuration-only, never assignable via commands). A command runs when the user's **effective tier** ≥ its **required tier** — the `CommandMetaData` override or the command's `DefaultPermissionTier`. The user's effective tier is the stored tier (0 if absent), raised to owner for the guild owner; in DMs it is owner tier (bot owner 4) without a database read.

**Escalation guard**: `!userpermission` can never modify the invoker's own tier, only users strictly below, and only assign tiers strictly below; `!commandpermission` only assigns required tiers strictly below the invoker's to commands currently strictly below. All lookups are keyed by guild id; the management commands act on the invoking guild only.

Tests: `TestPermissions.cs` (the scale, effective tiers, stored and overridden verdicts), `TestPermissionCommands.cs` (the two management commands and their escalation guards).

### 3.4 Persistence — EF Core model and migrations

`Connections/DatabaseContext.cs` — EF Core `DbContext` against PostgreSQL. Entities (all exposed through `Connections/IDatabaseContext.cs` so tests run against the InMemory provider):

| Entity | Key | Notes |
|---|---|---|
| `User` | `(GuildId, DiscordId)` | `PermissionTier`. |
| `CommandMetaData` | `(GuildId, CommandName)` | `PermissionTier`. |
| `GuildSetting` | `GuildId` | `CommandIdentifier` override, per-tier cooldown overrides (`Get/SetCooldownOverride` is the single mapping), `OperatorAgentTier` (§3.11), quote-of-the-day config (§3.17). |
| `Quote` | `(GuildId, Number)` | `Text` (1–2000), `AddedByDiscordId`, `CreatedAt` (§3.6). |
| `CustomResponse` | `(GuildId, Name)` | `Content` (1–1000), modifier, timestamps (§3.7). |
| `Reminder` | `(OwnerDiscordId, Number)` | `Message` (1–500), `DueAtUtc`, nullable `GuildId`/`RecurrenceInterval`/`FailedAtUtc` (§3.8, §3.14). |
| `BotSetting` | `Key` | Global key-value; first key: notification channel id (§3.13). |
| `BotBan` | `(GuildId, DiscordId)` | `BannedByDiscordId`, `CreatedAt`, `ExpiresAtUtc` (null = permanent) (§3.16). |

Migrations (`HambotV2/Migrations/`) are applied automatically on startup (`Database.Migrate()` before the gateway starts); an unreachable database is a critical log line and non-zero exit. Regenerate with `dotnet ef migrations add <Name>` from the `HambotV2/` directory; the `DesignTimeDatabaseContextFactory` avoids running `Program.Main`. Pre-`PerGuildScoping` rows keep the sentinel `GuildId = 0` (matches no real guild; re-grant per guild).

Tests: no dedicated suite — every feature suite runs against this model through `IDatabaseContext` over the InMemory provider; migrations are startup-applied, not unit-tested.

### 3.5 System commands — the general-purpose set

All in `Commands/SystemCommands/`, registered by `RegisterSystemCommands`; `PermissionPersistence` holds the shared upsert logic of the two permission commands.

| Command | Tier | Behavior |
|---|---|---|
| `hello` | 0 | "Hello World". |
| `help [name \| page]` | 0 | Grouped, paginated command list for the invoking user (see §3.19 for pagination); detailed help for a runnable command; unknown/insufficient-tier names get the generic "unavailable" reply so higher-tier commands are never revealed. |
| `commandpermission <name> [tier]` | 2 | Show/set a command's required tier; escalation-guarded; guild-only. |
| `userpermission <user> [tier]` | 2 | Show/set a user's tier (ids and mentions); escalation-guarded; guild-only. |
| `banuser <user> [duration]` / `unbanuser <user>` / `banlist` | 1 | Per-guild bot bans (§3.16); guild-only. |
| `settings [commandidentifier <v>\|reset \| cooldown <tier> <s>\|reset]` | 2 | Guild identifier and cooldown overrides (guard: tiers strictly below your own; bot owner unrestricted); guild-only. |
| `agenttier [<tier 0-4>\|clear]` | 3 | Operator-agent tier for the guild (§3.11); guild-only. |
| `version` | 0 | Version string (§3.9); DM-usable. |
| `health` | 4 | Health report (§3.13); DM-usable. |
| `notifychannel [<channel>\|clear]` | 4 | Notification channel (§3.13); DM-usable. |
| `presence [<type> <text...>\|clear]` / `bio [<text...>\|clear]` | 4 | Activity / bio via `IPresenceService` (§3.10); DM-usable; not persisted. |
| `avatar [<image-url>\|reset]` / `banner [<image-url>\|clear]` | 4 | Profile images (§3.10); DM-usable. |

Tests: no dedicated suite — each row is covered by the feature suite that describes it (§6).

### 3.6 Quotes — per-guild quote storage and recall

`Quote` entity, per-guild scoping by key. **Numbering**: 0-based sequential (`MAX(Number) + 1`); removal never renumbers, and removing the top quote lets the next add reuse that number. **Commands** (`Commands/QuoteCommands/`, all declining in DMs): `quote` (random, number-prefixed; empty guild gets a friendly message), `quote <number>` (exact fetch; unknown number is an in-channel error), `quote <text...>` (anything non-numeric — including out-of-`long`-range values — is added as a quote; > 2000 chars rejected), `searchquotes` (case-insensitive substring, number-prefixed, every match ranked most-relevant-first and paginated 10 per page with invoker-owned previous/next buttons, §3.19; plus word-level fuzzy matches ranked behind the substring hits — a quote whose whitespace-delimited word is within edit distance ≤ 2 of the whole term also matches, ties by quote number, §3.18, FR-150), `removequote` (moderator), `importquotes` (bot owner; see below), `quoteoftheday` (admin; §3.17). The random fetch is one count query plus an offset skip (provider-agnostic). Replies that would exceed Discord's 2000-char limit are trimmed with a trailing ellipsis (`QuoteCommand.SendFittedAsync`); stored text is never modified.

**`!importquotes`** — migration path for the old bot's `quoteSettings.json` (`{"quotes": [{"quote": "...", "date": "..."}]}`), run by the bot owner in the target guild with the file attached. The attachment is downloaded through `IAttachmentContentDownloader` (a shared-`HttpClient` abstraction, faked in tests). All-or-nothing validation: every entry non-empty and ≤ 2000 chars, the first invalid entry aborts with an in-channel error and nothing stored. Entries append in file order continuing the numbering; `CreatedAt` takes the legacy `date` when present. Re-running appends again (duplicates are the operator's responsibility).

Tests: `TestQuotes.cs` (numbering, adds and recall, search + fuzzy ranking + pagination, import, per-guild isolation, DM declines).

### 3.7 Custom command responses — user-defined text commands, per guild

`CustomResponse` entity, always looked up by the full `(GuildId, Name)` key. **Names**: one lowercase token matching `^[a-z0-9_-]{1,32}$` (`CustomResponseNames` helper). **Precedence**: built-ins always win — the handler consults custom responses only after the built-in lookup fails, and `addresponse` rejects built-in names. **Dispatch**: in a guild, an unknown token is looked up as a custom response and, on a hit, goes through the same permission (tier from `CommandMetaData`, default 0) and cooldown checks as a built-in; the audit event logs it as `Executed`. In DMs the "unavailable" reply stands. **Content is text-only**: invocations with attachments are rejected; links are stored and sent verbatim (Discord renders previews; the bot uploads nothing).

Commands (`Commands/ResponseCommands/`, guild-only): `addresponse <name> <content...>` (create-or-update, moderator), `removeresponse <name>` (moderator), `listresponses` (everyone).

Tests: `TestCustomResponses.cs` (name rules, built-in precedence, dispatch with permission and cooldown, content limits, DM declines).

### 3.8 Reminders — user-scoped timed reminders

`Reminder` entity keyed `(OwnerDiscordId, Number)` — user-scoped, usable in guilds and DMs. Per-user sequential numbering like quotes.

**Duration parsing** — `ReminderDurationParser`, a pure helper: leading number+unit tokens (`s`/`m`/`h`/`d`/`w`, abbreviations and long forms, compact and spaced, multiple tokens summed). The total must be positive; there is no min/max. The message is the remainder, ≤ 500 chars.

**Commands** (`Commands/ReminderCommands/`, all tier 0, DM-usable, always filtered by owner): `remindme <duration> <message...>` (persists, confirms; 10 pending cap; notifies the delivery signal), `reminders` (soonest first, ≤ 10), `cancelreminder <number>`.

**Delivery** — a reusable engine in `Scheduling/`, generic over the message kind; reminders are the first consumer:

- `ScheduledMessageDeliveryService<T>` — hosted loop on the injected `TimeProvider`. It snapshots the wake-up signal, queries the source's earliest pending due time, and sleeps until `min(earliest due, now + 10 s)` (a bounded fallback poll for drift/missed signals). Due messages go to the sender; each delivery is isolated with 3 retries / 2-s backoff, then recorded as failed. The signal snapshot before the query guarantees a message scheduled right after a read still wakes the loop.
- `ScheduledDeliverySignal` — singleton wake-up (`TaskCompletionSource`); commands notify it after save/delete so the loop re-schedules. It travels through the registration chain from `CommandService`.
- `Reminders/ReminderMessageSource.cs` — `IScheduledMessageSource` implementation over `IDatabaseContext` from fresh scopes. Delivered one-shots are deleted; recurring rows are re-scheduled (§3.14); retry-exhausted rows are marked failed (kept for audit). Outcomes are recorded per-row against current state, so a cancelled reminder is not resurrected.
- `Reminders/ReminderDmSender.cs` — DMs the owner on the shared gateway client (user cache first, REST fallback; an unresolvable user is a delivery failure).
- Restart recovery is by construction: the pending set is database state, so overdue reminders deliver on the first pass.

Tests: `TestReminders.cs` (duration parsing, persistence and caps, DM usage, the delivery engine over fakes).

### 3.9 Version reporting — embedded build version and `!version`

The version is the semver of the latest `v`-prefixed tag reachable from the built commit plus its 7-char hash, as build metadata (`v1.2.3` @ `1639324…` → `1.2.3+1639324`; untagged → `0.0.0+<hash>`; no git → `0.0.0+unknown`). The MSBuild target `ComputeBotVersion` in `HambotV2.csproj` embeds the raw pieces as `AssemblyMetadata` (`BotVersionTag`, `BotVersionCommit`, also settable via `-p:` for git-less builds); the pure `BotVersion.Format` composes the string, unit-tested. `Program.Main` logs `Bot version: <version>` right after the host is built — the line the deploy verification matches. `!version` (tier 0, DM-usable) replies with it.

Deployment wiring: the Dockerfile takes `ARG BOT_VERSION_TAG`/`BOT_VERSION_COMMIT`; compose maps them from the environment; `scripts/deploy.sh` passes the exact deployed commit's pieces and verifies the `Bot version:` line. Jenkins agent builds get them for free from the workspace git.

Tests: `TestVersion.cs` (format boundaries, the embedded metadata shape).

### 3.10 Discord presence & profile identity — one path for every identity change

A singleton `IPresenceService` (`Connections/PresenceService.cs`) is the single path for every change to the bot's identity — startup config, `!presence`/`!bio`/`!avatar`/`!banner`, the MCP `set_presence`/`set_bio` tools, and the planned LLM job. All applies run under one single-flight `SemaphoreSlim` (last request wins); requests made before gateway Ready are deferred to the Ready event (the service is resolved eagerly in `Program.Main`); apply failures are logged and never thrown out (commands surface them as the standard in-channel error; a failing guild never stops the others). The bio/avatar/banner become "managed" only once configured — an absent startup config leaves the Discord-side state untouched, so a restart never wipes a hand-set bio.

The service talks to Discord through the `IPresenceGateway` seam (a narrow slice of the gateway client, mockable in tests); `DiscordPresenceGateway` wraps the shared `DiscordSocketClient`. Discord.Net 3.20.2 covers all needed routes: the per-guild member route (`ModifyCurrentUserAsync`) for bio/avatar/banner, the global user route for the avatar; there is no global banner route for bots.

Startup config: optional `BotConfiguration:Presence` (`Type`: playing/watching/listening/competing; `Text` ≤ 128 chars) and `BotConfiguration:Bio` (≤ 190 chars, line breaks allowed), parsed purely in `PresenceStartupConfiguration.TryParse`; invalid values fail fast naming the key.

Commands (all tier 4, DM-usable, not persisted — a restart reapplies the startup config): `!presence` show/set/clear; `!bio` (raw text via `GetRaw`, applied in every member guild); `!avatar`/`!banner` (`Commands/SystemCommands/`, sharing `ProfileImageCommandBase`) — the image comes from exactly one attachment or a single URL, downloaded via `IImageContentDownloader` and validated by `ProfileImages` (PNG/JPEG/GIF signature sniffing, ≤ 10 MB) before anything is applied; `!avatar reset` / `!banner clear` remove. No-argument invocations reply with the current image URL. MCP operators can set images via `run_command` with the URL form.

Tests: `TestPresence.cs` (config parsing, service against a fake gateway, commands) and `TestProfileImages.cs`.

### 3.11 MCP server — operator-agent integration

`Mcp/McpServerService.cs` — opt-in hosted service (`Mcp:Enabled`, default false; `Mcp:ListenUri`, default `http://0.0.0.0:2525`; `Mcp:ApiKey`) starting an in-process Kestrel app that maps the session endpoint at `{ListenUri}/mcp` with `ModelContextProtocol.AspNetCore` 2.3.0. Active only when enabled *with* a key; the startup log names the state; the same state feeds `!health`. Compose publishes the port loopback-only.

**Auth** — `Mcp/McpBearerAuthentication.cs` middleware: every request needs `Authorization: Bearer <Mcp:ApiKey>`; anything else is 401 before any tool logic.

**Tools** — thin wrappers in `Mcp/McpTools.cs` behind `IMcpTools`, reading Discord through the `IMcpDiscordClient` seam so they run in unit tests without a connection: `bot_status`, `list_guilds`, `list_channels(guildId)` (cached channels: id/name/type, for channel-id discovery), `list_commands` (registry: name/tier/description/usage), `get_recent_messages(channelId, limit ≤ 50)`, `send_message(channelId, text ≤ 2000, plain text)`, `set_presence`, `set_bio`, `run_command`, and the member lookup tools (below).

**`run_command`** — virtual invocation through the `IVirtualCommandRunner` seam (`Commands/VirtualCommandRunner.cs`, shared with the slash surface): a `VirtualCommandMessage` carrying the guild's first cached text channel, the bot's own user as author, and the `OperatorAgentId`/`OperatorAgentTier` grant. The agent acts at the guild's **operator-agent tier** (`GuildSetting.OperatorAgentTier`, default the lowest tier, set via `!agenttier` — owner-tier, guild-only; the agent can never change its own ceiling). The invocation runs the full pipeline (identifier resolution, dispatch, permission, cooldown keyed on the bot's id, audit) and the captured reply is returned as the tool result — never posted to a channel. Every future command is exposed for free.

**Member lookup tools (FR-139–FR-143) — `search_users(guildId, query, limit ≤ 25)`, `get_user_profile(guildId, userId)`, `get_user_quotes(guildId, userId)`, `get_user_messages(guildId, userId, limit ≤ 50)`.** Read-only lookups of one specific member: `search_users` is the discovery step — Discord's REST member search over username/display name/nickname resolves a name the agent encountered (a message author, "who is X") to a user id, so a full member list is unnecessary (and members who left are deliberately not findable). Identity comes from one `IMcpDiscordClient` member read (`IGuild.GetUserAsync`, cache-then-REST — the member cache stays empty without the `GuildMembers` intent; roles resolved from `RoleIds` against the guild's role cache, `@everyone` excluded; null when the user is not a member → the profile is reported without the identity line; no new gateway intents). The bot-side records reuse the pipeline's own helpers over a fresh scoped transient `IDatabaseContext` per call (the singleton tool layer never holds a context): the stored permission tier (`Users.Find`, default 0 — the record, not an effective-tier computation), the active ban via `BotBans.FindActiveAsync` (an expired record lifts itself on sight) with `DescribeLifetime` wording, the quote count with the most recent quote (number-descending — the per-guild sequential numbering *is* the age order), and the last-modified custom-response names capped at 10 with a "+N more" count. The quotes tool returns the member's quotes number-descending, `#<n>:`-prefixed like `!quote`, capped at 50 with "+N more"; the messages tool walks the guild's text channels (a dedicated seam read; news channels derive from text channels), fetches one page per channel, filters by author, merges newest-first, and truncates at 50 — an unknown guild is a clean error like `run_command`'s, and a channel that fails to load is skipped, so a partial result beats a failed call. Reminders are out by design (private state, FR-61). The REST candidate set is ranked locally with the shared fuzzy matcher (§3.18) — distance of the query to the closest of username/display name/nickname, closest first, alphabetical tiebreak by username — so the closest match leads; the set, cap, and error semantics are unchanged (FR-151). Nothing is written, executed through the pipeline, or posted — no `TierOverrides` are involved, so the operator-agent tier (FR-120) never gates reads (the bearer key is the only check: a guild sets what the agent may *do*, not what it may read of what the bot already holds); during maintenance mode (FR-98) each tool declines with the pipeline's maintenance notice as an error result, and the bot compiles and stores nothing — interpreting the records is the calling agent's job.

**Failure isolation** — each call logs one structured event (tool name, outcome, no content); failures become error results and never crash the endpoint or the bot. Maintenance mode needs no special casing: the notice is captured like any reply.

Tests: `TestMcp.cs`, `TestAgentTier.cs` — the lookup tools run over EF InMemory through the real `IDatabaseContext` behind the fake `IMcpDiscordClient` (profile records and the default-0 fallback, the expired ban lifting itself, the response-name cap, the left-member degradation, search results/cap/missing query, quotes ordering and cap, message author filtering/cap/channel skips, the unknown-guild errors, and the maintenance declines).

### 3.12 LLM integration, phase 1 — presence updates **(planned)**

`Llm/ILlmClient.cs` abstracts a chat completion; `Llm/MistralLmClient.cs` implements it against the Mistral API (`Llm:ApiKey`, `Llm:Model` default `mistral-small-latest`). No key → every LLM feature inactive. `Llm/PresenceUpdateService.cs` — opt-in hosted loop (`Llm:PresenceUpdates:Enabled`, interval `Llm:PresenceUpdates:IntervalMinutes` default 60) building a prompt from the persona seed (`Llm:Persona`, with a shipped, configuration-overridable default), asking for a single-line activity ≤ 128 chars, validating, and applying via `IPresenceService`. Failures keep the current presence and retry next tick. `Llm:BioUpdates:Enabled` does the same for the bio (≤ 190 chars). Usage is accounted per feature per UTC day (`!llm status`, `!health`); a daily budget (`Llm:DailyRequestBudget`, default 500) hard-stops calls for the rest of the UTC day (watchdog alerts); accounting and the `!llm off/on` kill switch (tier 4) are in-memory, so a restart restores the configured state. Later phases (conversation participation, memory) build on this client and the MCP endpoint; see [`roadmap.md`](roadmap.md).

Tests (planned): a fake `ILlmClient`, no network (prompt construction, validation boundaries, `FakeTimeProvider` scheduling, failure path, inactive-without-key).

### 3.13 Watchdog, health reporting & operator notifications — degradation made loud

**Health snapshot** — a singleton provider aggregates version (`BotVersion`), uptime, gateway state, a cheap database probe, pending reminder count, notification channel, backup status (from the shared status file), and MCP state; the LLM fields join when §3.12 lands. Presence/bio are deliberately not part of it. Consumers: `!health` (tier 4, DM-usable) and the watchdog.

**Watchdog** — `Watchdog/WatchdogService.cs`, hosted loop (`Watchdog:CheckIntervalMinutes`, default 5) evaluating: gateway down past `GatewayDownAlertMinutes` (default 10); backup failed validation / overdue (interval + 60-min grace) / failed-or-overdue restore test; LLM budget exhausted or three consecutive LLM failures (dormant until §3.12); maintenance mode; notification channel unreachable. Each condition alerts on its rising edge, re-alerts at most hourly (`ReAlertMinutes`), and announces recovery once — by DM to the configured bot owner *and* via the notification publisher; without a configured owner, logs only.

**Notification publisher** — the channel id lives in `BotSetting` (runtime override via `!notifychannel`, tier 4, DM-usable; `Notifications:ChannelId` is the configuration default). `Watchdog/NotificationPublisher.cs` posts watchdog alerts/recoveries, command failures, retry-exhausted reminders, maintenance/restore outcomes, and its own failures (which additionally DM the owner), rate-limited (`Notifications:MaxPostsPerMinute`, default 5) with identical consecutive messages collapsed. A failed post never affects the originating feature; startup criticals stay log-only.

**Backup status file** — the sidecar writes `backups/.backup-status` (last successful backup, last validation, last restore-test result) into the shared volume; it is the only channel between the sidecar and the in-bot health surface. The sidecar seeds from an existing file on startup, and the watchdog gives a never-recorded restore test the interval-plus-grace window, so restarts cannot trip false alerts.

Tests: `TestWatchdog.cs` (fakes + `FakeTimeProvider`).

### 3.14 Recurring reminders — the `every` form

Extends §3.8 with a nullable `RecurrenceInterval` (null = one-shot). `remindme every <duration> <message...>` reuses the duration parser (a start-index overload skips the token); the interval must clear `Reminders:MinRecurrenceMinutes` (default 10). On delivery the row is re-scheduled from the *actual delivery time* (`TimeProvider`), so downtime shifts the schedule instead of bursting; retry exhaustion marks it failed like a one-shot. Numbers are stable across deliveries; `!reminders` marks recurring entries; `!cancelreminder` deletes permanently. A separate per-user cap (`Reminders:MaxRecurring`, default 5, `0` disables the form) is counted independently of the 10 one-shot slots. Settings are parsed by `CommandService` from `IConfiguration` and flow through the registration chain (`ReminderSettings.FromConfiguration`, defaults on absent/invalid values); intervals render via `ReminderIntervalFormat`.

Tests: `TestReminders.cs` (the `every` form, re-scheduling from the delivery time, the recurring cap).

### 3.15 Discord application commands — slash commands

Opt-in (`SlashCommands:Enabled`, default false). Eligible: administrative/configuration commands (permissions, settings, bans, the operator-agent tier, presence/bio/profile images, health, notifications, backups, `quoteoftheday`), commands acting solely on the invoker's own state (the reminder commands), and `!help` (as `/help`). Community-facing commands (`quote`, `searchquotes`, `removequote`, `importquotes`, custom responses, `!version`, `!hello`) stay text-only. Eligibility is declared per command via `ICommand.ApplicationCommand` (`SlashCommands/ApplicationCommandMetadata.cs`), including typed options, the single free-form `arguments` fallback, or none. On the first gateway Ready, `SlashCommandService` builds one definition per eligible command (validating Discord's registration limits), pushes them as one bulk global overwrite (idempotent), and logs critical on failure, leaving the text surface intact.

**Execution** — the interaction is converted to its equivalent text command line (`SlashCommandLineBuilder`), then run through the real `CommandHandler` via the shared `IVirtualCommandRunner` seam (also consumed by the MCP `run_command`): identical permissions, cooldown keys (shared with the text surface), audit, and error semantics; the guild's effective identifier prefixes the virtual line.

**Replies** — acknowledged with an ephemeral defer; every reply (output and denials alike) is posted as an ephemeral followup, visible only to the invoker. Reply text is bit-identical to the text surface — the surfaces differ only in visibility. Captured replies carry message components, so interactive buttons work on ephemeral replies (§3.19).

Tests: `TestSlashCommands.cs`.

### 3.16 User bot bans — moderation

The `BotBan` record stops bot abuse without kicking the user from the server: guild-scoped (a ban in one guild has no effect elsewhere; DMs are never blocked, so banned users keep DM features like reminders). Re-banning updates the record.

**Enforcement** — pipeline step 2 (§3.2), on both surfaces (slash and agent invocations run through the same handler): while an active record exists, every invocation is dropped with *no reply at all* (audited as `Banned`) — not even the "unavailable" reply or a custom response, so an abuser has no reply surface to farm. Exemptions: the guild owner, the bot owner, and operator-agent invocations. Expiry is lazy: a record whose `ExpiresAtUtc` has passed is deleted on sight (pipeline check or `!banlist`) and the user proceeds as unbanned.

**Commands** (`Commands/SystemCommands/`, moderator tier, guild-only, Moderation category, slash-eligible): `banuser <user> [duration]` (reminder duration parser for the temporary form; the `userpermission` escalation guard applies — never the invoker, guild owner, or bot owner), `unbanuser` (deliberately unguarded — lifting a restriction is a relief action), `banlist` (soonest expiry first, ≤ 10 entries, expired lifted on sight).

Tests: `TestBotBans.cs`.

### 3.17 Quote of the day — scheduled per-guild publication

A regularly published random quote keeps a channel alive, per guild, on a fixed schedule.

**Configuration** — new `GuildSetting` columns (migration `AddQuoteOfTheDay`): target channel id (null = off — the only activation switch), template (default `quote of the day: {quote}`), interval minutes (default 24 h), and the anchor UTC instant (default 00:00 UTC). The schedule is entirely UTC — no per-guild time zones. The interval floor is deployment config: `QuoteOfTheDay:MinIntervalMinutes` (default 1).

**Scheduling** — fixed wall-clock boundaries: anchor + whole multiples of the interval, in UTC (a daily interval publishes at the same time every day). `QuoteOfTheDay/QuoteOfTheDaySchedule.cs` holds the pure tick math; the next due time is always derived from configuration, so restarts need no persisted due time.

**Publication loop** — `QuoteOfTheDay/QuoteOfTheDayPublicationService.cs`, a dedicated hosted `BackgroundService` (not the `Scheduling/` engine: a failed post must *skip* its boundary, not retry, and the pending set is derived, not stored). Every 10 s it loads configured guilds and keeps an in-process per-guild memory of the last consumed boundary:

- First sight (startup, restart, re-enable, changed schedule — the record's value equality detects the change) baselines to the current boundary *without* publishing; boundaries passed while down are skipped — no catch-up burst, at most one publication per boundary.
- A new boundary publishes once: one random quote (the shared `QuoteCommand.GetRandomQuoteAsync`) through the template, trimmed at Discord's limit with an ellipsis. A guild with no quotes skips with a log line. A failed post (deleted channel, missing permission, Discord error) is logged and skips the boundary — the schedule is unchanged and a failing channel never disables the feature. Maintenance mode pauses passes; the first pass after it re-baselines.

Transport is the `IQuoteOfTheDayChannelSender` seam: the channel is resolved by id on the shared client at publication time (no upfront permission validation, no extra gateway intents).

**Command** — `!quoteoftheday` (admin tier, guild-only, slash-eligible): no args report the config or "off"; `channel <#channel|id>` activates, `channel clear` deactivates; `template <text...>` (non-empty, ≤ 500 chars, must contain `{quote}`); `<due time> <interval>` sets the schedule (single duration parsed via the reminder parser's `TryParseFirst`, its end becomes the anchor; the interval must be whole minutes ≥ the floor) — e.g. `quoteoftheday 5h 1d`. A schedule change re-baselines and takes effect at the new anchor; channel/template changes leave the schedule untouched. Settings flow through the registration chain like the reminder limits.

Tests: `TestQuoteOfTheDay.cs`.

### 3.18 Unknown-command suggestions — "did you mean?"

A typo is a dead end no more: the unknown-token reply gains one pointer toward the intended command (FR-144–FR-148, NFR-31).

**Matcher** — a shared pure helper (`Commands/FuzzyMatcher.cs`), not a suggestion-private utility (FR-149): the bounded two-row Levenshtein distance plus a best-selection over candidates (lowest distance wins, deterministic tiebreak), with each consumer passing its own qualification rule. The distance function needs no early-exit complexity — names and words are short. The command-suggestion path is its first consumer (`Commands/CommandSuggestions.cs` builds on it): inputs are the attempted token (already lowercased by `CommandParsing`) and the candidate names — the registry's built-in names (in memory, no database) plus, for a guild invocation, one query over the guild's custom-response names (they dispatch from the same fallthrough, FR-145; names only, no content loaded). Its qualification rule is the FR-146 one: distance ≤ 2, or a prefix hit with a token of ≥ 3 characters; at most one name is returned. Case-insensitivity holds by construction: the token arrives lowercased, built-in names are lowercase by the registration contract, and custom-response names by the `CustomResponseNames` pattern. The search consumers reuse the same helper for typo tolerance: `!searchquotes` word-level matches (§3.6, FR-150) and `search_users` result ranking (§3.11, FR-151).

**Pipeline placement** — the final fallthrough of `CommandHandler` step 3 (§3.2), only after both the built-in and the custom-response lookups have failed; an exact match can never reach it by construction. Tier filtering runs *before* matching, exactly as the dispatch path computes it: a candidate's effective required tier is the per-guild `CommandMetaData` override or the command's built-in default (`IPermissionsService`), with DM invocations at the built-in defaults (FR-25) and virtual invocations at the operator-agent grant (FR-120); a candidate above the invoker's effective tier is dropped — the never-reveal rule of `!help <name>` applied to suggestions (FR-147). The reply becomes two lines — the unchanged "unavailable" line plus `Did you mean !quote?` with the invocation's effective identifier (FR-148) — or stays as-is when nothing qualifies. Because the suggestion is part of the channel reply, the MCP `run_command` capture picks it up with no special handling, and the audit event stays `UnknownCommand` with no content logged (FR-148). The slash surface never sees this path: its invocations are built from the registered definitions, so an unknown name cannot be invoked on it at all — and Discord's own autocomplete already guides users there. Maintenance mode keeps declining before this path; banned users are already dropped upstream.

Tests: `TestFuzzyMatcher.cs` — the matcher's boundaries (symmetric distance, empty-string edges, tie-breaking, rejected and empty candidate sets) and the pipeline path through the real handler over the real registry: typo → suggestion, prefix token → the closest longer command, unrelated garbage → plain reply, tier exclusion in both directions (a moderator-tier invoker never gets the bot-owner command suggested; the same typo at bot-owner tier does), DM scope (built-ins only), custom-response candidates and their raised tiers. `TestQuotes.cs` — fuzzy search matching and ranking (misspelled term, substring precedence, distance ties by quote number, the unchanged no-match reply). `TestMcp.cs` — member-search ranking (closest first, nickname as the closest name, alphabetical ties) and a virtual `run_command` invocation of a typo returning the suggestion in its captured replies.

### 3.19 Interactive pagination — previous/next buttons on paginated lists

A list longer than one Discord message is either cut off or forces the user to retype a page argument; the buttons remove both. Two replies paginate today — the `!help` command list (§3.5) and the `!searchquotes` results (§3.6) — and share one button design: a multi-page reply carries an invoker-owned previous/next button pair, a single-page reply carries none. The shared primitives live in `Commands/PaginationButtons.cs`: the gateway-free click abstraction (`IPaginationButton` — user, custom id, defer, ephemeral reply, in-place modification, so click handling is unit-testable without a live connection) and the button pair itself (custom ids supplied per feature, disabled at the bounds). Each feature keeps its own pagination core composing its custom ids; every custom id encodes the owning invoker, so paging is invoker-owned everywhere — a foreign click is declined ephemerally, without running the pipeline or touching the message.

**Help pagination** — `Commands/SystemCommands/HelpPagination.cs`. `HelpCommand` attaches buttons whose custom ids encode the list's owner and target page; pages are group-aware. The invoker's own click is turned back into a `help <page>` invocation through the shared virtual-invocation seam (§3.11) — full pipeline semantics: permissions, cooldown, audit — and the message is updated in place with the new page and rebuilt buttons; only a real page reply may rewrite the message, and a declined or out-of-range reply goes to the clicker ephemerally. Legacy ownerless buttons parse as owned by nobody and are declined.

**Quote search pagination** — `Commands/QuoteCommands/QuoteSearchPagination.cs` + the `QuoteSearchBacklog` DI singleton. `SearchQuotesCommand` ranks every match most-relevant-first and stores the ranked backlog — up to the 100 most relevant matches — per invoker (one entry per invoker; a new search replaces it). A click renders the requested page from that snapshot: no re-query, no re-rank, no pipeline run — the message is rewritten in place with rebuilt buttons whose custom ids encode the invoker, the search, and the target page. A button of a replaced or lost search (restart) gets an ephemeral run-it-again reply.

**Gateway glue** — `SlashCommands/ButtonPaginationService.cs` subscribes the shared client's button events and dispatches each click by custom-id prefix to its feature's core. Attached unconditionally: the buttons ride on text-surface messages regardless of `SlashCommands:Enabled`, and the slash surface's captured replies carry components (§3.15), so buttons work on ephemeral replies too.

Tests: `TestHelp.cs` (help pages and click handling), `TestQuotes.cs` (search ranking, paging, the backlog, click handling).

## 4. Configuration

- `appsettings.json` (committed): `BotConfiguration:BotName`, `LogFormat`, `CommandIdentifier` (default `!`), `CooldownSecondsByTier` (missing entries default to 3; `0` disables; invalid entries treated as absent), `Serilog` section (levels + console and daily-file sinks to `logs/`), and the optional presence/bio startup keys (§3.10).
- `HambotV2/appsecrets.json` (gitignored, source runs only): `Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, optional `Mcp` section. Environment variables take precedence (re-added after the secrets file); the file is excluded from the Docker build context, so images are secret-free.
- Required keys (`Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, `BotConfiguration:BotName`, `LogFormat`, `CommandIdentifier`) are validated at startup (`ConfigurationValidator`); missing keys exit non-zero naming the key.
- `Permissions:BotOwnerDiscordId` (`BOT_OWNER_DISCORD_ID` in `.env`) — optional; the bot owner holds tier 4 in every guild.
- `Mcp:*` (§3.11): `Enabled` (default false), `ListenUri` (default `http://0.0.0.0:2525`), `ApiKey` (required when enabled). Compose: `MCP_ENABLED`/`MCP_API_KEY`/`MCP_LISTEN_URI`, port loopback-only.
- `Watchdog:*` / `Notifications:*` (§3.13): `CheckIntervalMinutes` (5), `GatewayDownAlertMinutes` (10), `ReAlertMinutes` (60), `ChannelId` (optional), `MaxPostsPerMinute` (5). Compose: `WATCHDOG_*`, `NOTIFY_CHANNEL_ID`.
- `Reminders:*` (§3.14): `MinRecurrenceMinutes` (10), `MaxRecurring` (5, `0` disables). Absent/invalid → defaults.
- `QuoteOfTheDay:MinIntervalMinutes` (default 1) — the deployment-level interval floor.
- `SlashCommands:Enabled` (default false) — the slash surface. The pagination buttons (§3.19) are always active.
- Planned (§3.12): `Llm:ApiKey`, `Llm:Model`, `Llm:Persona`, `Llm:PresenceUpdates:Enabled`/`IntervalMinutes`, `Llm:BioUpdates:Enabled`, `Llm:DailyRequestBudget`.
- Backup scheduling is compose-side environment, not bot config: `BACKUP_INTERVAL` (24 h), `BACKUP_RETENTION` (7), `BACKUP_TEST_INTERVAL` (30 d), `BACKUP_RCLONE_REMOTE` + S3 credentials. The bot reads only `Backups:Directory` for the in-bot backup commands.

## 5. Deployment

Containerized via `HambotV2/Dockerfile` (multi-stage .NET 10 build → publish → runtime-only image on the ASP.NET Core base — required by the in-process MCP endpoint — plus `postgresql-client-17` for the in-bot backup commands). Build context is the repo root; the image is secret-free. `docker-compose.yml` brings up PostgreSQL (healthcheck + `db-data` volume), the bot (waits for db health, MCP port published loopback-only), and the backup sidecar (`backups` volume shared with the bot).

```bash
cp .env.example .env   # fill in DISCORD_BOT_TOKEN and POSTGRES_PASSWORD
docker compose up -d --build
```

Startup order: config validation → migration → gateway login; any failure is a critical log line and non-zero exit. Compose defaults the connection string to the `db` service (`host.docker.internal` for plain containers).

### 5.1 CI/CD pipeline — Jenkins auto-deploy

The `Jenkinsfile` (declarative, "Pipeline script from SCM") has three stages: **Build** (Release, nullable warnings as errors), **Test** (`dotnet test`, TRX archived), and **Deploy** — gated on version tags `v<major.minor.patch>` only; ordinary pushes are built and tested but never deployed. The Deploy stage SSHes to the server, checks out the built commit in the deploy clone, and runs `scripts/deploy.sh`, which: snapshots the running image as a rollback point, builds the new image before touching any container, replaces only changed containers (secrets only from the server's gitignored `.env`), polls the startup lines (`Database migrations applied successfully`, `Discord gateway connection established.`, `Bot version: <expected>`), rolls back and exits non-zero if they don't appear, and on success prunes dangling images and writes `.deploy-info`.

One-time server preparation:

```bash
sudo useradd -m hambot-deploy && sudo usermod -aG docker hambot-deploy   # docker group ≈ root; accepted for this single-server deploy
sudo mkdir -p /opt/hambotv2 && sudo chown hambot-deploy /opt/hambotv2
sudo -u hambot-deploy git clone <origin-url> /opt/hambotv2
cd /opt/hambotv2 && sudo -u hambot-deploy cp .env.example .env   # fill in; the pipeline never touches this file
# Jenkins: SSH key pair for hambot-deploy (no passphrase) → authorized_keys; private key stored as an
# "SSH Username with private key" credential; needs the SSH Agent plugin and DEPLOY_HOST/USER/DIR/CREDENTIAL in the Jenkinsfile.
```

Full setup guide: [`jenkins-cicd.md`](jenkins-cicd.md).

### 5.2 Database backups

**Sidecar** — a dedicated `backup` compose service (built from `scripts/backup/`, `postgres:17-alpine` + `rclone`), so backups run even while the bot is down. `run-backup.sh` loops: `pg_dump -Fc` into the `backups` volume → validate (`pg_restore --list` + size floor; an invalid dump is deleted and counts for nothing) → prune beyond retention (only after a good dump) → push offsite via rclone when configured (a failed push is logged and harmless) → write `.backup-status` → sleep. On `BACKUP_TEST_INTERVAL` (default monthly, and immediately on a fresh deployment) it restores the newest dump into a scratch database, verifies the dump's own restore-catalog tables exist, drops the scratch db, and records the outcome. Credentials come only from the gitignored `.env`.

**Restore** — `scripts/restore-backup.sh <dump-file>` is the authoritative path for a broken bot/database: stop the bot → recreate the database → `pg_restore` → restart → verify the startup lines.

**In-bot commands** — the runtime image carries pg tools and mounts the `backups` volume (the sidecar opens it `chmod 1777` so the non-root bot can write). `Backups/ProcessBackupManager` (behind `IBackupManager`, faked in tests) converts the Npgsql connection string to a `postgresql://` URI for libpq. Commands (`Commands/BackupCommands/`, all tier 4, DM-usable): `!backup` (on-demand dump + name/size reply), `!backups` (newest first, ≤ 10), `!restorebackup <name>` → confirm. A restore enters **maintenance mode** (FR-98): a singleton flag that declines every other command (and MCP `run_command` — it runs through the same pipeline) and pauses the delivery engine and quote-of-the-day loop so nothing is marked failed; pooled connections are cleared before, and the guild-settings cache is invalidated after. The command path is the convenient revert for a healthy bot; the script remains authoritative.

### 5.3 Weekly dependency security scan

`Jenkinsfile.security` — a separate scheduled Pipeline job (cron `0 4 * * 0`, controller timezone), deliberately *not* the main multibranch job and holding no credentials at all, so no scan run can reach production. Stages: **Build** (same command/warning policy, no tests) → **SBOM** (pinned `dotnet CycloneDX` per project, plus the pinned jq binary into the workspace) → **Image** (`docker build` + `docker save` the deployment image, never pushed or started) → **Scan** (Trivy over the SBOMs and the saved tarball, JSON reports, `--exit-code 0` so findings never fail a stage but tool/DB failures fail the build) → **Report** (jq severity summary; HIGH/CRITICAL counts in the deployed tree — `HambotV2` SBOM + image, `BotTests` excluded — mark the build UNSTABLE via the `unstable` step, never FAILED). Artifacts under `security/` are archived per build (~26-build retention). Pipeline-only: no bot code, config, or `BotTests`; verified by a live run of the job.

The Jenkins container needs the host Docker socket + CLI as volume mounts for the Image stage (root-equivalent access, the accepted §5.1 trade-off).

## 6. Testing

- `BotTests/` (MSTest + Moq) runs without a database or token: Moq doubles the Discord message surface; command/permission tests run against the EF Core InMemory provider through `IDatabaseContext` (isolated database per test); time-sensitive logic uses `FakeTimeProvider`.
- Coverage: each design section names its covering suites in its `Tests:` line. Cross-cutting contracts are asserted once, not per feature: the command registration contract in `TestCommandInfrastructure`, the pipeline outcomes and audit event in `TestCommandHandler`, startup config validation in `TestConfigurationValidator`.
- Build and run: `dotnet build HambotV2.sln` / `dotnet test HambotV2.sln`. The build is warning-free; CI treats warnings as errors.

## 7. Conventions

- C# with nullable reference types and implicit usings; `LangVersion` latest in the test project.
- Bot-internal types are `internal` (tests rely on `InternalsVisibleTo`, which also grants Moq's proxy assembly).
- Command names must be lowercase.
- DI lifetimes: `CommandService` singleton; `CommandHandler`, `PermissionsService`, `DatabaseContext` transient; handler resolved per message and disposed after use.

## 8. Known Design Considerations / Current Limitations

- CI/CD: the `Jenkinsfile` builds, tests, and auto-deploys version tags only; ordinary pushes (including `master`) never deploy. A second scheduled pipeline (`Jenkinsfile.security`, §5.3) runs the weekly dependency scan and never deploys.
