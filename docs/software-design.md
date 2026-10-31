# Software Design

The design of HambotV2, as implemented. Sections marked **(planned)** describe roadmap work not yet built; everything else is as-built. Requirements are numbered in [`software-requirements.md`](software-requirements.md) (FR/NFR).

## 1. System Overview

Discord chat bot on .NET 10 (generic host): **Discord.Net 3.20.2** gateway, **PostgreSQL** via **EF Core 10 / Npgsql**, custom command pipeline with per-guild tiered permissions.

```
                    ┌─────────────┐  gateway   ┌──────────────────────────────────────┐
   Discord users ◄──►│   Discord   │◄──────────►│  HambotV2 — .NET 10 generic host     │◄── MCP tools (opt-in, bearer key, §3.11)
                    │    cloud    │            │  DiscordClient + hosted loops +      │    operator agent
                    └─────────────┘            │  command pipeline (§2, §3)           │
                                               └──────────┬───────────────┬─────────────┘
                                                          │ EF Core/Npgsql   │ pg_dump / pg_restore
                                                          ▼                  ▼
                                                  ┌───────────────┐  ┌───────────────────┐
                                                  │ PostgreSQL 17 │  │ backup sidecar    │──► offsite via rclone (optional)
                                                  │ (db-data vol) │  │ (backups vol +    │    writes .backup-status (§5.2)
                                                  └───────────────┘  │  .backup-status)  │
                                                                     └───────────────────┘
   Jenkins: Build + Test on every push; Deploy on v* tags only (§5.1); weekly security scan (§5.3)
```

| Project | Purpose |
|---|---|
| `HambotV2/` | The bot: hosted services, command pipeline, EF Core model and migrations. |
| `BotTests/` | MSTest + Moq unit tests (`InternalsVisibleTo`); run without a database or token. |

Key dependencies: Discord.Net, EF Core/Npgsql, Microsoft.Extensions.Hosting, Serilog (console + file sinks), ModelContextProtocol.AspNetCore (MCP), MSTest + Moq (tests).

## 2. Runtime Architecture

```
┌────────────────┐  message   ┌─────────────────────┐
│ Discord gateway│ ──────────►│ DiscordClient       │ BackgroundService, logs in with
│ (Discord.Net)  │            │ Connections/        │ Secrets:BotToken, subscribes MessageReceived
└────────────────┘            └──────────┬──────────┘
                                         │ messages starting with the guild's command identifier
                                         ▼
                              ┌─────────────────────┐      ┌───────────────────┐
                              │ CommandHandler      │─────►│ PermissionsService│ (verdict + effective tier)
                              │ transient, per msg  │      └───────────────────┘
                              └──────────┬──────────┘
                                         │ resolve + execute
                              ┌──────────▼──────────┐      ┌───────────────────┐
                              │ CommandService      │      │ DatabaseContext   │ EF Core/Npgsql, transient,
                              │ singleton registry  │      │ (→IDatabaseContext)│ disposed after every message
                              └─────────────────────┘      └───────────────────┘

Hosted services (all in the same process):
  DiscordClient · ScheduledMessageDeliveryService<ScheduledReminderMessage> (§3.8)
  · QuoteOfTheDayPublicationService (§3.17) · WatchdogService (§3.13) · McpServerService (§3.11, opt-in)
  · ContextPipelineHost (§3.20, planned)
```

### 2.1 Composition root — `HambotV2/Program.cs`

Registers `appsecrets.json` then environment variables (env wins), replaces logging with Serilog (`ReadFrom.Configuration`), wires DI, validates required keys and applies migrations before starting the gateway (§4, §5). DI lifetimes:

| Lifetime | Registrations |
|---|---|
| Singleton | Shared `DiscordSocketClient` (intents below; container-disposed; also used by the reminder DM sender and the quote-of-the-day sender); `CommandService`; `GuildSettingsService` (per-guild identifier/tier cache); `IVirtualCommandRunner` + `SlashCommandService` (§3.15); `QuoteSearchBacklog` + `ButtonPaginationService` (§3.19); `ICooldownService` + `TimeProvider.System`; `IScheduledDeliverySignal` + reminder source/sender (§3.8); quote-of-the-day sender (§3.17); `IPresenceService` + gateway seam + `IImageContentDownloader` (§3.10); maintenance/backup/notification/health/gateway-status set (§3.13, §5.2); MCP client/tools/status set (§3.11). `IPresenceService` is resolved eagerly in `Main` so its Ready subscription exists before the gateway starts. |
| Transient | `ICommandHandler` → `CommandHandler` (per message, disposed after use), `IPermissionsService`, `DatabaseContext` (also mapped onto `IDatabaseContext`). |
| Hosted | The five services listed above. |

## 3. Component Design

### 3.1 Discord connection — gateway message intake

`Connections/DiscordClient.cs` — logs in and forwards messages to a freshly resolved `ICommandHandler` when they start with the guild's effective identifier (per-guild override via `IGuildSettingsService`; global default in DMs). Ignores bot/webhook messages, including its own. Also attaches the shared client's surface handlers: the pagination button handler unconditionally (§3.19), and the slash interaction handlers when `SlashCommands:Enabled` (§3.15).

- Intents: `Guilds | GuildMessages | DirectMessages | MessageContent` — only what the bot uses; never `AllUnprivileged` (it warns about unused intents).
- Gateway logs bridge to the host logger via `BotConfiguration:LogFormat`; connect/disconnect logged; keep-alive logs state every 5 min; clean logout on shutdown.

Tests: identifier-prefix routing and ignore rules via the pipeline suites (`TestCommandHandler.cs`, `TestCommandInfrastructure.cs`); the hosted service itself is thin Discord.Net glue.

### 3.2 Command pipeline — from message to command execution

Runs once per message, in `Commands/`:

```
message with identifier prefix
   │
   ▼
 1. DM scope?      → invoker acts at owner tier (4); commands keep their default tiers; no guild state read/written
 2. BotBan active? → drop silently, no reply at all (outcome Banned; exemptions: guild owner, bot owner, operator agent)
 3. Resolve: built-in registry → guild custom responses (§3.7) → "unavailable" reply (+ "did you mean?" §3.18)
          bare identifier → ignored silently (Ignored); maintenance mode declines everything but the restore confirmation (§5.2)
 4. Permission: effective tier ≥ required tier → else "not permitted" (DeniedPermission)
 5. Cooldown: per (guild, user, command) → else "too quickly, wait N s" (Throttled); denials are not throttled
 6. Execute with a CommandContext
   │  any exception → generic in-channel error (never exception details) + log + notification channel (§3.13)
   ▼
one structured audit event per message (finally-covered): CommandName, Outcome, GuildId, ChannelId, UserId, DurationMs — no content
```

Outcomes: `Executed`, `DeniedPermission`, `UnknownCommand`, `Ignored`, `Throttled`, `Banned`, `Failed`.

Contracts and helpers:

- **`ICommand`** — `Name` (lowercase), `Description`, `Usage`, `DefaultPermissionTier`, `Category`, `ExecuteCommand(IMessage, ICommandContext)`. Help text lives on the implementation so it cannot drift.
- **`CommandContext`** — per-message `IDatabaseContext`, `GuildId` (null in DMs), effective `CommandIdentifier`, `TierOverrides` (guild owner, configured bot owner, operator agent for virtual invocations), `GetUserEffectiveTier`, `IGuildSettingsService`.
- **`CommandService`** — singleton `Dictionary<string, ICommand>` registry, lazily filled by `RegisterAllCommands`. `RegisterCommand` is the contract chokepoint (rejects non-lowercase names, missing help, invalid tier/category — enforced by the registration-contract test). `TryGetCommand`: identifier as exact prefix, name = first non-whitespace run after it, lowercased (`! quote` resolves to nothing).
- **Registration** — `RegisterAllCommands` delegates to `RegisterSystemCommands`, `RegisterQuoteCommands`, `RegisterResponseCommands`, `RegisterReminderCommands`; the chain receives the shared `IScheduledDeliverySignal` so reminder commands can wake the delivery loop (§3.8).
- **`CommandArguments.cs`** — shared typed parser: whitespace-split tokens (no quoting; one token = one argument), `Count`/`Get`/`GetJoined`/`GetRaw` (verbatim remainder)/`TryGetString`/`TryGetInt`/`TryGetDiscordUserId` (raw id or `<@id>`). New commands must use it.
- **`GuildSettingsService.cs`** — singleton; resolves and caches each guild's effective identifier and operator-agent tier; setters upsert the row *and* refresh the cache; never holds a `DbContext`.
- **`CooldownService.cs`** — in-memory `ConcurrentDictionary` of timestamps per (guild, user, command), injected `TimeProvider`. Window: guild per-tier `GuildSetting` override, else `BotConfiguration:CooldownSecondsByTier` (default 3 s; `0`/negative disables). Tier 4 is never bound by a guild override; DMs use sentinel guild 0. Runs after the permission check, before execution.

One failure never stops later messages; the per-message `DatabaseContext` is disposed on all paths.

Tests: `TestCommandHandler.cs` (step order, outcomes, audit, failure paths, DM scope), `TestCommandInfrastructure.cs` (registry contract), `TestCommandArguments.cs`, `TestCooldowns.cs`, `TestGuildSettings.cs`.

### 3.3 Permissions — tiered access

`Commands/PermissionsService.cs`:

```
4  bot owner     Permissions:BotOwnerDiscordId — configuration-only, never assignable via commands
3  guild owner   automatic in its guild
2  admin         ┐ assignable only by a strictly higher-tier invoker,
1  moderator     ┘ and only to users/commands strictly below the invoker (escalation guard)
0  everyone      default when no row exists
```

A command runs iff the user's **effective tier** ≥ its **required tier** (`CommandMetaData` override, else the command's `DefaultPermissionTier`). Effective tier = stored tier (0 if absent), raised to 3 for the guild owner; in DMs it is 4 without a database read. All lookups are keyed by guild id; the management commands act on the invoking guild only.

Tests: `TestPermissions.cs` (scale, effective tiers, verdicts), `TestPermissionCommands.cs` (`!userpermission`/`!commandpermission` + guards).

### 3.4 Persistence — EF Core model and migrations

`Connections/DatabaseContext.cs` (exposed as `IDatabaseContext` so tests run on the InMemory provider):

```
GuildSetting 1 ──< User            key (GuildId, DiscordId)            PermissionTier
             1 ──< CommandMetaData key (GuildId, CommandName)         PermissionTier
             1 ──< Quote           key (GuildId, Number)              Text 1–2000, AddedByDiscordId, CreatedAt (§3.6)
             1 ──< CustomResponse  key (GuildId, Name)                Content 1–1000, modifier, timestamps (§3.7)
             1 ──< BotBan          key (GuildId, DiscordId)           BannedBy, CreatedAt, ExpiresAtUtc null = permanent (§3.16)
Reminder     key (OwnerDiscordId, Number) — user-scoped, not guild-scoped: Message 1–500, DueAtUtc, nullable GuildId/RecurrenceInterval/FailedAtUtc (§3.8, §3.14)
BotSetting   key Key — global key-value: notification channel id (§3.13), global persona override (§3.12)
GuildSetting columns: CommandIdentifier override, per-tier cooldown overrides, OperatorAgentTier (§3.11), quote-of-the-day config (§3.17), nullable Persona (§3.12, FR-156)
```

Migrations (`HambotV2/Migrations/`) apply automatically on startup (`Database.Migrate()` before the gateway); an unreachable database is a critical log line + non-zero exit. Regenerate with `dotnet ef migrations add <Name>` from `HambotV2/` (`DesignTimeDatabaseContextFactory` avoids running startup). Pre-`PerGuildScoping` rows keep sentinel `GuildId = 0` (matches no real guild; re-grant per guild).

Tests: no dedicated suite — every feature suite runs against this model through `IDatabaseContext` over InMemory.

### 3.5 System commands — the general-purpose set

All in `Commands/SystemCommands/`, registered by `RegisterSystemCommands`; `PermissionPersistence` holds the shared upsert logic of the two permission commands.

| Command | Tier | Behavior |
|---|---|---|
| `hello` | 0 | "Hello World". |
| `help [name \| page]` | 0 | Grouped, paginated list for the invoker (§3.19); detailed help for a runnable command; unknown/higher-tier names get the generic "unavailable" reply so they are never revealed. |
| `commandpermission <name> [tier]` | 2 | Show/set a command's required tier; escalation-guarded; guild-only. |
| `userpermission <user> [tier]` | 2 | Show/set a user's tier (id or mention); escalation-guarded; guild-only. |
| `banuser <user> [duration]` / `unbanuser <user>` / `banlist` | 1 | Per-guild bot bans (§3.16); guild-only. |
| `settings [commandidentifier <v>\|reset \| cooldown <tier> <s>\|reset \| persona <text...>\|clear]` | 2 | Identifier, cooldown, and per-guild persona overrides (cooldown guard: tiers strictly below your own, bot owner unrestricted; persona: §3.12, FR-156); guild-only. |
| `agenttier [<tier 0-4>\|clear]` | 3 | Operator-agent tier for the guild (§3.11); guild-only. |
| `version` | 0 | Version string (§3.9); DM-usable. |
| `health` | 4 | Health report (§3.13); DM-usable. |
| `notifychannel [<channel>\|clear]` | 4 | Notification channel (§3.13); DM-usable. |
| `presence [<type> <text...>\|clear]` / `bio [<text...>\|clear]` | 4 | Activity / bio via `IPresenceService` (§3.10); DM-usable; not persisted. |
| `avatar [<image-url>\|reset]` / `banner [<image-url>\|clear]` | 4 | Profile images (§3.10); DM-usable. |

Tests: each row is covered by the feature suite that describes it (§6).

### 3.6 Quotes — per-guild quote storage and recall

`Quote` entity, scoped by key. **Numbering**: 0-based sequential (`MAX(Number)+1`); removal never renumbers; removing the top quote lets the next add reuse that number. **Random fetch**: one count query + offset skip (provider-agnostic). Replies over Discord's 2000-char limit are trimmed with a trailing ellipsis (`QuoteCommand.SendFittedAsync`); stored text is never modified.

Commands (`Commands/QuoteCommands/`, all decline in DMs):

| Command | Tier | Behavior |
|---|---|---|
| `quote` | 0 | Random, number-prefixed; empty guild gets a friendly message. |
| `quote <number>` | 0 | Exact fetch; unknown number is an in-channel error. |
| `quote <text...>` | 0 | Any non-numeric input (incl. out-of-`long` values) is added; > 2000 chars rejected. |
| `searchquotes <text...>` | 0 | Case-insensitive substring matches, ranked most-relevant-first, paginated 10/page with invoker-owned prev/next buttons (§3.19); word-level fuzzy matches (edit distance ≤ 2 to the whole term) ranked behind substring hits, ties by quote number (§3.18, FR-150). |
| `removequote <number>` | 1 | Remove. |
| `importquotes` | 4 | Migration from the old bot's `quoteSettings.json` (file attached); see below. |
| `quoteoftheday ...` | 2 | Quote-of-the-day config (§3.17). |

**`!importquotes`** — all-or-nothing import of `{"quotes": [{"quote": "...", "date": "..."}]}` via `IAttachmentContentDownloader` (shared-`HttpClient` seam, faked in tests): every entry non-empty and ≤ 2000 chars or the first invalid entry aborts with nothing stored; entries append in file order continuing the numbering; `CreatedAt` takes the legacy `date` when present. Re-running appends again.

Tests: `TestQuotes.cs` (numbering, add/recall, search + fuzzy + pagination, import, per-guild isolation, DM declines).

### 3.7 Custom command responses — user-defined text commands, per guild

`CustomResponse` entity, looked up by the full `(GuildId, Name)` key.

- **Names**: one lowercase token matching `^[a-z0-9_-]{1,32}$` (`CustomResponseNames`).
- **Precedence**: built-ins always win — custom responses are consulted only after the built-in lookup fails; `addresponse` rejects built-in names.
- **Dispatch**: an unknown token in a guild goes through the same permission (tier from `CommandMetaData`, default 0) and cooldown checks as a built-in; audited as `Executed`. In DMs the "unavailable" reply stands.
- **Content is text-only**: invocations with attachments are rejected; links are stored and sent verbatim (Discord renders previews).

Commands (`Commands/ResponseCommands/`, guild-only): `addresponse <name> <content...>` (create-or-update, tier 1), `removeresponse <name>` (tier 1), `listresponses` (tier 0).

Tests: `TestCustomResponses.cs`.

### 3.8 Reminders — user-scoped timed reminders

`Reminder` entity keyed `(OwnerDiscordId, Number)` — usable in guilds and DMs, always filtered by owner; per-user sequential numbering like quotes.

**Duration grammar** (`ReminderDurationParser`, pure): leading number+unit tokens (`s`/`m`/`h`/`d`/`w`, abbreviations and long forms, compact or spaced, multiple tokens summed) → positive total, no min/max; the message is the remainder, ≤ 500 chars. E.g. `remindme 1h30m check the oven`.

Commands (`Commands/ReminderCommands/`, all tier 0, DM-usable): `remindme <duration> <message...>` (persists + confirms; 10 pending cap; notifies the delivery signal), `reminders` (soonest first, ≤ 10), `cancelreminder <number>`. The `every` form is §3.14.

**Delivery** — a generic engine in `Scheduling/`, one message kind at a time (reminders today):

```
   IScheduledDeliverySignal (wake-up, singleton; commands notify after save/delete)
        │
        ▼
 ┌────────────────────────────────────────────┐
 │ ScheduledMessageDeliveryService<T>         │  hosted loop on the injected TimeProvider
 │  snapshot signal → query earliest due      │
 │  sleep until min(earliest due, now + 10 s) │  (bounded fallback poll for drift)
 │  due rows → sender, each isolated:         │
 │  3 retries / 2 s backoff, then mark failed │
 └───────────────────┬────────────────────────┘
        one-shot: delete row   │   recurring: re-schedule from actual delivery time (§3.14)
        retry-exhausted: FailedAtUtc set, row kept for audit; a cancelled reminder is never resurrected
        ReminderDmSender: DMs the owner on the shared gateway client (cache first, REST fallback)
        Restart recovery by construction: the pending set is database state, so overdue reminders deliver on the first pass
```

`ReminderMessageSource` implements `IScheduledMessageSource` over fresh-scoped `IDatabaseContext`s. Outcomes are recorded per row against current state.

Tests: `TestReminders.cs` (parsing, persistence/caps, DM usage, the engine over fakes).

### 3.9 Version reporting — embedded build version and `!version`

Semver of the latest `v`-prefixed tag reachable from the built commit + 7-char hash, as build metadata: `v1.2.3` @ `1639324…` → `1.2.3+1639324`; untagged → `0.0.0+<hash>`; no git → `0.0.0+unknown`. The MSBuild target `ComputeBotVersion` in `HambotV2.csproj` embeds the raw pieces as `AssemblyMetadata` (also settable via `-p:` for git-less builds); the pure `BotVersion.Format` composes the string (unit-tested). `Program.Main` logs `Bot version: <version>` right after the host is built — the line deploy verification matches (§5.1). `!version` (tier 0, DM-usable) replies with it. Docker: `ARG BOT_VERSION_TAG`/`BOT_VERSION_COMMIT`, mapped by compose and passed by `scripts/deploy.sh`, which verifies the log line.

Tests: `TestVersion.cs`.

### 3.10 Discord presence & profile identity — one path for every identity change

```
startup config · !presence/!bio/!avatar/!banner · MCP set_presence/set_bio (§3.11) · planned LLM job (§3.12)
        │
        ▼
 IPresenceService  — single-flight SemaphoreSlim (last request wins); requests before gateway Ready
 │   deferred to the Ready event; failures logged, never thrown; bio/avatar/banner become "managed"
 │   only once configured, so a restart never wipes a hand-set value
 ▼
 IPresenceGateway (mockable seam) → DiscordPresenceGateway → shared DiscordSocketClient
     per-guild route (ModifyCurrentUserAsync) for bio/avatar/banner; global route for avatar; no global banner route for bots
```

- Startup config: optional `BotConfiguration:Presence` (`Type` playing/watching/listening/competing, `Text` ≤ 128) and `BotConfiguration:Bio` (≤ 190, line breaks ok), parsed purely in `PresenceStartupConfiguration.TryParse`; invalid values fail fast naming the key.
- Commands (all tier 4, DM-usable, not persisted — a restart reapplies the startup config): `!presence` show/set/clear; `!bio` (raw text, applied in every member guild); `!avatar`/`!banner` (shared `ProfileImageCommandBase`) — image from exactly one attachment or one URL, downloaded via `IImageContentDownloader`, validated by `ProfileImages` (PNG/JPEG/GIF signature sniffing, ≤ 10 MB) before anything is applied; `reset`/`clear` remove; no-arg replies with the current URL.

Tests: `TestPresence.cs`, `TestProfileImages.cs`.

### 3.11 MCP server — operator-agent integration

```
MCP client ──HTTP──► McpBearerAuthentication (401 without "Authorization: Bearer <Mcp:ApiKey>")
                        │
                        ▼
              Kestrel {Mcp:ListenUri}/mcp  (in-process, opt-in: Mcp:Enabled + Mcp:ApiKey; default http://0.0.0.0:2525)
                        │
              ┌─────────┴────────────────────────────────────────────┐
              ▼                                                    ▼
        IMcpTools (thin wrappers over IMcpDiscordClient   run_command ──► IVirtualCommandRunner
        seam + scoped IDatabaseContext per call):          │            ──► full pipeline (identifier,
        bot_status · list_guilds · list_channels(guildId)  │            dispatch, permission, cooldown,
        list_commands · get_recent_messages(ch, ≤50)      │            audit); captured reply returned,
        send_message(ch, ≤2000) · set_presence · set_bio   │            never posted to a channel
        search_users · get_user_profile ·                  ▼
        get_user_quotes · get_user_messages        VirtualCommandMessage: guild's first cached
                                                   text channel, bot itself as author,
                                                   OperatorAgentTier grant (§ !agenttier)
```

- Every command is exposed to the agent for free through `run_command`; the agent acts at the guild's **operator-agent tier** (`GuildSetting.OperatorAgentTier`, default lowest; set via `!agenttier`, owner tier, guild-only; the agent can never raise its own ceiling).
- Member lookup tools (FR-139–FR-143): `search_users(guildId, query, ≤25)` — REST member search over username/display name/nickname, ranked locally by the shared fuzzy matcher (§3.18) so the closest match leads (FR-151); left members are deliberately not findable. `get_user_profile` — identity via one `IMcpDiscordClient` member read (cache-then-REST; no `GuildMembers` intent needed), stored tier (`Users.Find`, default 0), active ban (`BotBans.FindActiveAsync`, expired lifts itself), quote count + most recent quote, last-modified custom-response names (≤ 10 + "+N more"). `get_user_quotes` — number-descending (numbering = age order), `#<n>:`-prefixed, ≤ 50 + "+N more". `get_user_messages` — walks the guild's text channels, one page each, filters by author, merges newest-first, ≤ 50; a failed channel is skipped, an unknown guild is a clean error. Reminders are out by design (private state, FR-61).
- Reads are gated only by the bearer key — the operator-agent tier gates *actions*, not reads of what the bot already holds; nothing is written, executed through the pipeline, or posted.
- During maintenance mode each tool declines with the pipeline's maintenance notice (§5.2). Each call logs one structured event (tool, outcome, no content); failures become error results and never crash the endpoint or the bot.

Tests: `TestMcp.cs`, `TestAgentTier.cs` — lookups run over EF InMemory through the real `IDatabaseContext` behind a fake `IMcpDiscordClient`.

### 3.12 LLM integration — foundation toolkit + phase 1: presence updates **(planned)**

Phase 1 is not a one-off presence generator: it ships a small **LLM foundation** in `Llm/` (registered as singletons, §2.1) that every later stage — conversation participation, memory & personality (roadmap phase #2) — hooks into instead of growing its own client, prompt assembly, or guardrails. What starts those later stages and what feeds their prompts is not hardcoded: registered triggers and collectors (§3.20).

```
┌─────────────────────────── LLM foundation (`Llm/`) ───────────────────────────┐
│  every LLM feature ──► LlmGate ──► ILlmClient ──► MistralLmClient             │
│   (phase 1: presence/   │  kill switch (!llm off/on, FR-104) + daily budget  │
│    bio loop; later:     │  Llm:DailyRequestBudget (FR-103), checked before   │
│    chat, memory jobs)   │  every call; usage recorded per feature name       │
│                         └─► ILlmUsageTracker (per-feature per-UTC-day,       │
│                             FR-102) → !llm status, !health, watchdog (§3.13)  │
│  prompt assembly: LlmPromptBuilder — effective persona (chain below, FR-155/156) + bot │
│    name/version (§3.9) + current UTC date/time + current presence + optional │
│    context blocks; the trust rules (FR-154) are enforced here, not in        │
│    features                                                                  │
│  context input: ChatContextCollector — bounded recent guild chat, never DMs │
│    (the first registered collector, §3.20)                                  │
│  output check: LlmOutputValidator — per-surface limits (presence ≤ 128      │
│    single line · bio ≤ 190 · later: channel message ≤ 2000)                  │
└──────────────────────────────────────────────────────────────────────────────┘
   phase 1 consumer: PresenceUpdateService → validated output → IPresenceService (§3.10)
   later consumers are started by registered triggers (§3.20) and apply their own sinks
   (channel send, memory tables) — never run_command (§3.11)
```

| Piece | What it is | How later stages hook in |
|---|---|---|
| `Llm/ILlmClient.cs` + `Llm/MistralLmClient.cs` | The only seam to the model: `ChatAsync(IReadOnlyList<LlmMessage>, …)` → content + token usage; Mistral chat completions via `Llm:ApiKey`/`Llm:Model` (default `mistral-small-latest`); no key → inactive, every feature no-ops (FR-92); interface keeps everything unit-testable without network (FR-95). | Send system + history as `LlmMessage`s through the same call; phase 1 sends a single user prompt. |
| `LlmGate` | Mandatory entry point — features never call `ILlmClient` directly; the gate checks kill switch + UTC-day budget first, then calls and accounts (FR-103/FR-104). | Register a feature name (`presence`, `bio`, later `chat`, `memory`) and inherit kill switch, budget, accounting, and the watchdog alerts (§3.13) by construction. |
| `Llm/ILlmUsageTracker.cs` | In-memory per-feature per-UTC-day counters (requests, tokens in/out), UTC rollover via `TimeProvider` (FR-102); restart restores the configured state. | Read your feature's counters in `!llm status`/`!health`; nothing to build. |
| `LlmPromptBuilder` | Composable prompt assembly: the effective persona (resolution chain below) + bot name/version + UTC now + current presence + optional context blocks. Trust rules live here (FR-154): untrusted content is embedded as data-only — never stored, logged, or executed, never triggers commands, tools, database writes, or persona/config changes; output constrained to the requested shape (no quoting, no user or channel names); secrets (NFR-3) are never prompt input. | Add context blocks (memories, history); the trust rules apply unchanged. |
| `ChatContextCollector` | Shared bounded chat reader: newest messages from the bot's readable guild channels, `Llm:ChatContext:MaxMessages` (default 20) / `Llm:ChatContext:MaxCharacters` (default 4000), never DMs (FR-154). | Registered as an `IContextCollector` (§3.20); phase 1 includes it only via `Llm:PresenceUpdates:IncludeChat` (default false). |
| `LlmOutputValidator` | Per-surface output rules: presence = non-empty, single line, ≤ 128 chars (FR-93); bio = non-empty, ≤ 190 chars, line breaks allowed (FR-114). | Register additional surfaces (channel message ≤ 2000). |

- Phase 1 feature — `Llm/PresenceUpdateService.cs`: opt-in hosted loop (`Llm:PresenceUpdates:Enabled`, `Llm:PresenceUpdates:IntervalMinutes` default 60) that builds the prompt through the builder, calls through the gate, validates, and applies via `IPresenceService` (§3.10); `Llm:BioUpdates:Enabled` refreshes the bio in the same tick.

**Commands** (`Commands/LlmCommands/`, registered via `RegisterLlmCommands` in the chain; always registered — without `Llm:ApiKey` they just report the features inactive, FR-92; slash-eligible as administrative commands per §3.15, so they run through the standard pipeline with cooldown and audit):

| Command | Tier | Behavior |
|---|---|---|
| `llm` / `llm status` | 4 | Report: active/inactive (key present), kill-switch state, per-feature usage for the current UTC day (requests, tokens in/out — FR-102), daily budget used/limit, consecutive failures. DM-usable. |
| `llm off` / `llm on` | 4 | Set/clear the in-memory kill switch — the gate refuses every call; replies with a confirmation. A restart restores the configured state (FR-104). DM-usable. |
| `persona` | 2 | No args: show the effective *global* persona and which layer is active. `persona <text...>`: set the global runtime override (persisted in `BotSetting`, FR-155). `persona clear`: remove it → fall back to config/shipped seed. DM-usable. |

The per-guild layer is `settings persona <text...>|clear` (§3.5, tier 2, guild-only, persisted in `GuildSetting.Persona`, FR-156).

**Persona resolution** — every LLM run resolves its persona in order: guild-scoped run with a `GuildSetting.Persona` override → that override; else the global runtime override (`BotSetting`); else `Llm:Persona` config; else the shipped default seed (FR-99/155/156). Phase 1's presence/bio job is a global run, so it uses the global chain; per-guild overrides are consumed by guild-scoped runs from phase B on (§3.20). Persona text: non-empty, ≤ 2000 chars. Note: tier 2 is per-guild, so any guild's admin can change the *global* persona via `!persona` — accepted per FR-155; `!settings persona` is the per-guild knob.

Apart from the persona, phase 1 has no per-setting commands: settings are config-only, the kill switch is the only other runtime control.

**Settings** (all under `Llm:`; absent/invalid → defaults, except `ApiKey`, which only gates activity):

| Key | Default | Meaning |
|---|---|---|
| `ApiKey` | *(absent)* | Secret (`appsecrets.json` / env `Llm__ApiKey`, NFR-3); absent → every LLM feature inactive (FR-92). |
| `Model` | `mistral-small-latest` | Chat model (FR-92). |
| `Persona` | shipped default seed | Persona seed, configuration-overridable (FR-99). |
| `PresenceUpdates:Enabled` | false | Opt-in for the presence job (FR-93). |
| `PresenceUpdates:IntervalMinutes` | 60 | Tick interval, floor 1 min (FR-93). |
| `PresenceUpdates:IncludeChat` | false | Include bounded recent guild chat in the prompt (FR-154). |
| `ChatContext:MaxMessages` / `ChatContext:MaxCharacters` | 20 / 4000 | Chat-context bounds; never DMs (FR-154). |
| `BioUpdates:Enabled` | false | Refresh the bio in the same tick (FR-114). |
| `DailyRequestBudget` | 500 | Per-UTC-day request hard stop (FR-103); `0` disables the feature set like an absent key. |

**Runtime state** (in-memory only; a restart restores the configured state): the kill switch, the per-feature per-UTC-day usage counters (FR-102), and the consecutive-failure counter. Exposed via `!llm status` and `!health` (FR-105); the watchdog alerts on budget exhaustion and on three consecutive failures (FR-106, §3.13).

- Shared failure semantics (FR-94): any client error or invalid output → keep the current state, log, count toward the watchdog's consecutive-failure alert (§3.13), retry next tick; nothing ever crashes the bot.
- Built-in LLM vs. MCP (§3.11): complementary paths. Built-in features (phase 1 and later) use this toolkit with the raw Mistral key; the operator agent can already drive the bot via MCP (`send_message`, `run_command`). Later stages pick per feature which path fits; LLM output never goes through `run_command`.

Tests (planned): fake `ILlmClient`, no network (`FakeTimeProvider` scheduling, validation boundaries, failure path, inactive-without-key; gate: budget suspension/rollover, kill switch, per-feature accounting; prompt builder: trust-rule wrapping, chat-context bounds, DM exclusion).

### 3.13 Watchdog, health reporting & operator notifications — degradation made loud

```
 WatchdogService (hosted loop, Watchdog:CheckIntervalMinutes default 5)
   evaluates: gateway down > GatewayDownAlertMinutes (10) · backup failed validation / overdue
   (interval + 60 min grace) / failed-or-overdue restore test · LLM budget exhausted or 3
   consecutive LLM failures (dormant until §3.12) · maintenance mode · notification channel unreachable
        │  alert on rising edge, re-alert ≤ hourly (ReAlertMinutes 60), announce recovery once
        ▼
 NotificationPublisher ──► notification channel (BotSetting key, runtime override !notifychannel, config
   Notifications:ChannelId) AND DM to the configured bot owner; without an owner, log only.
   Rate-limited (Notifications:MaxPostsPerMinute default 5); identical consecutive messages collapsed;
   a failed post never affects the originating feature. Also carries: command failures, retry-exhausted
   reminders, maintenance/restore outcomes.

 Health snapshot (singleton, feeds !health tier 4 + the watchdog): version (§3.9), uptime, gateway state,
   cheap DB probe, pending reminder count, notification channel, backup status, MCP state, LLM state
   when §3.12 lands (active, kill switch, budget used, per-feature requests). Presence/bio deliberately excluded.
```

**Backup status file**: the sidecar writes `backups/.backup-status` (last successful backup, validation, restore test) into the shared volume — the only channel between sidecar and bot; seeded on sidecar startup, and a never-recorded restore test gets the interval-plus-grace window so restarts cannot trip false alerts.

Tests: `TestWatchdog.cs` (fakes + `FakeTimeProvider`).

### 3.14 Recurring reminders — the `every` form

Extends §3.8 with nullable `RecurrenceInterval` (null = one-shot). `remindme every <duration> <message...>` reuses the duration parser (start-index overload skips the token); the interval must clear `Reminders:MinRecurrenceMinutes` (default 10). On delivery the row is re-scheduled from the *actual delivery time* (`TimeProvider`), so downtime shifts the schedule instead of bursting; retry exhaustion marks it failed like a one-shot. Numbers are stable across deliveries; `!reminders` marks recurring entries; `!cancelreminder` deletes permanently. Separate per-user cap `Reminders:MaxRecurring` (default 5; `0` disables the form), independent of the 10 one-shot slots. Settings flow from `IConfiguration` through the registration chain (`ReminderSettings.FromConfiguration`, defaults on absent/invalid); intervals render via `ReminderIntervalFormat`.

Tests: `TestReminders.cs`.

### 3.15 Discord application commands — slash commands

Opt-in (`SlashCommands:Enabled`, default false). Execution path — an interaction becomes its equivalent text line and runs through the *real* pipeline, so permissions, cooldowns, audit, and error semantics are identical on both surfaces:

```
s slash interaction ──► SlashCommandLineBuilder ──► virtual text line (prefixed with the guild's identifier)
        │                                          │
        │ acknowledged with an ephemeral defer;    ▼
        │ every reply (output and denials) is an  IVirtualCommandRunner ──► CommandHandler (§3.2)
        └──────────── ephemeral followup ◄───────── captured reply — bit-identical text to the text surface,
                                                     carries components, so buttons work on ephemeral replies (§3.19)
```

- **Eligibility** is declared per command via `ICommand.ApplicationCommand` (typed options, a single free-form `arguments` fallback, or none): administrative/configuration commands (permissions, settings, bans, agent tier, presence/bio/images, health, notifications, backups, `quoteoftheday`), invoker-scoped commands (reminders), `/searchquotes`, `/version`, `/help`. Community-facing commands (`quote`, `removequote`, `importquotes`, custom responses, `hello`) stay text-only.
- On the first gateway Ready, `SlashCommandService` builds one definition per eligible command (validating Discord's limits) and pushes them as one bulk global overwrite (idempotent); failure logs critical and leaves the text surface intact.

Tests: `TestSlashCommands.cs`.

### 3.16 User bot bans — moderation

`BotBan` stops bot abuse without kicking the user from the server: guild-scoped (no effect elsewhere); DMs are never blocked, so banned users keep DM features like reminders. Re-banning updates the record.

- **Enforcement** — pipeline step 2 (§3.2), on both surfaces: while an active record exists, every invocation is dropped with *no reply at all* (audited `Banned`) — not even "unavailable" or a custom response, so an abuser has no reply surface to farm. Exemptions: guild owner, bot owner, operator-agent invocations. Expiry is lazy: a passed `ExpiresAtUtc` is deleted on sight and the user proceeds as unbanned.
- **Commands** (tier 1, guild-only, slash-eligible): `banuser <user> [duration]` (reminder duration parser for the temporary form; the escalation guard of `userpermission` applies — never the invoker, guild owner, or bot owner), `unbanuser` (deliberately unguarded — lifting a restriction is a relief action), `banlist` (soonest expiry first, ≤ 10, expired lifted on sight).

Tests: `TestBotBans.cs`.

### 3.17 Quote of the day — scheduled per-guild publication

```
UTC timeline:   anchor ──┬─────────┬─────────┬────►   boundaries = anchor + whole multiples of the interval
                        t0        t1        t2
             first sight of a guild/boundary baselines WITHOUT publishing; boundaries passed while
             down are skipped (no catch-up burst); each new boundary publishes at most once
             (failed post → logged, boundary skipped, schedule unchanged; empty guild → log only)
```

- **Configuration** (new `GuildSetting` columns, migration `AddQuoteOfTheDay`): target channel id (null = off — the only activation switch), template (default `quote of the day: {quote}`), interval minutes (default 24 h), anchor UTC instant (default 00:00 UTC). Entirely UTC — no per-guild time zones. Deployment floor: `QuoteOfTheDay:MinIntervalMinutes` (default 1). The next due time is always derived from configuration, so restarts need no persisted due time.
- **Loop** — `QuoteOfTheDayPublicationService`, a dedicated hosted `BackgroundService` (not the `Scheduling/` engine: a failed post must skip its boundary, not retry). Every 10 s it loads configured guilds and keeps the last consumed boundary per guild in memory (record value-equality detects schedule changes → re-baseline). Maintenance mode pauses passes; the first pass after it re-baselines. Transport: `IQuoteOfTheDayChannelSender` resolves the channel by id on the shared client at publication time (no extra intents). Quote = the shared `QuoteCommand.GetRandomQuoteAsync`, through the template, trimmed at Discord's limit with an ellipsis.
- **Command** — `!quoteoftheday` (tier 2, guild-only, slash-eligible): no args report config or "off"; `channel <#channel|id>` / `channel clear`; `template <text...>` (non-empty, ≤ 500, must contain `{quote}`); `<due time> <interval>` sets the schedule (single duration via the reminder parser's `TryParseFirst`; its end becomes the anchor; interval whole minutes ≥ floor) — e.g. `quoteoftheday 5h 1d`. A schedule change re-baselines; channel/template changes leave the schedule untouched.

Tests: `TestQuoteOfTheDay.cs`.

### 3.18 Unknown-command suggestions — "did you mean?"

- **Matcher** — shared pure helper `Commands/FuzzyMatcher.cs` (bounded two-row Levenshtein + best-selection, lowest distance wins, deterministic tiebreak; each consumer passes its own qualification rule — FR-149). First consumer: `CommandSuggestions.cs` — candidates are the registry's built-in names plus, in a guild, one query of the custom-response names (FR-145); qualification: distance ≤ 2, or prefix hit with a token ≥ 3 chars; at most one name. Case-insensitive by construction. Reused by `!searchquotes` word-level fuzzy matching (FR-150) and `search_users` ranking (FR-151).
- **Pipeline placement** — the final fallthrough of `CommandHandler` step 3, only after built-in and custom-response lookups both failed. Tier filtering runs *before* matching exactly as dispatch computes it (per-guild override or built-in default; DM = defaults; virtual = operator-agent grant) — a candidate above the invoker's effective tier is dropped, so higher-tier commands are never revealed (FR-147). Reply: the unchanged "unavailable" line plus `Did you mean !quote?` with the invocation's identifier (FR-148), or unchanged when nothing qualifies. The MCP `run_command` capture picks it up with no special handling; the audit stays `UnknownCommand`. The slash surface never sees this path (its names come from the registered definitions; Discord's autocomplete guides instead). Maintenance and bans decline upstream.

Tests: `TestFuzzyMatcher.cs` (matcher boundaries + the pipeline path through the real handler/registry), `TestQuotes.cs` (fuzzy search ranking), `TestMcp.cs` (search ranking, virtual typo invocation).

### 3.19 Interactive pagination — previous/next buttons on paginated lists

```
paginated reply carries invoker-owned prev/next buttons (custom id encodes invoker + target page);
single-page replies carry none. Two features paginate: !help (§3.5) and !searchquotes (§3.6).

click ──► ButtonPaginationService (subscribed on the shared client, attached unconditionally — buttons work
          │   on text-surface messages and on ephemeral slash replies alike)
          ▼
   feature core: foreign click → ephemeral decline, no pipeline run, message untouched
          │  own click:
          ├─ help: re-invoked as virtual "help <page>" through IVirtualCommandRunner → full pipeline
          │   (permissions, cooldown, audit); message updated in place with the new page + rebuilt buttons
          └─ search: page rendered from the QuoteSearchBacklog snapshot (≤ 100 ranked matches per invoker,
              replaced by each new search) — no re-query, no pipeline run; a lost/replaced search gets an
              ephemeral run-it-again reply
```

Shared primitives in `Commands/PaginationButtons.cs`: `IPaginationButton` (gateway-free click abstraction — user, custom id, defer, ephemeral reply, in-place modify — so click handling is unit-testable) and the button pair (custom ids per feature, disabled at bounds).

Tests: `TestHelp.cs`, `TestQuotes.cs`.

### 3.20 Context pipeline — registered triggers and collectors **(planned)**

Later LLM stages (conversation participation, memory — roadmap phase #2) need many context sources feeding one prompt path. The pipeline generalizes §3.12 into two registries. **Both registration points are plain DI `IEnumerable<T>` in `Program.cs` — adding a source is one interface plus one `AddSingleton` line; the orchestrator, prompt builder, and host never change.**

```
┌─ IContextTrigger — sources that START a run ──────────┐   ┌─ IContextCollector — sources that FEED the prompt ───┐
│ ScheduledTrigger    interval tick on TimeProvider      │   │ ChatContextCollector   recent guild chat (UNTRUSTED, §3.12)
│                   (presence/bio now; memory summary   │   │ MemoryCollector        per-guild memory tables (trusted, phase C)
│                   job later)                           │   │ QuoteCollector · UserProfileCollector · …              │
│ MessageTrigger      new message in a per-guild        │   └────────────────────────────▲───────────────────────────┘
│                   opted-in guild, cadence-bounded     │                                │ ContextSlice(name, content, trust)
│                   (≤ 1 unsolicited reply per N msgs / │   ┌────────────────────────────┴─────────────┐
│                   M minutes — phase B)                │   │ LlmPipeline.RunAsync(ContextRequest)     │ singleton orchestrator
└────────────────┬──────────────────────────────────────┘   │  1. run enabled collectors, each isolated │
                 │ fire — WHEN + request scope only;        │     (failure → log + skip slice, never   │
                 │  a trigger never builds prompts or calls │     fail the run)                        │
                 └──────────────────►                        │  2. LlmPromptBuilder assembles the prompt:│
                                                             │     untrusted slices only in data-marked  │
                                                             │     regions; secrets never input (FR-154)│
                                                             │  3. LlmGate: kill switch, UTC-day budget, │
                                                             │     per-feature accounting (§3.12)        │
                                                             │  4. ILlmClient call → output validated    │
                                                             │     per surface (LlmOutputValidator)      │
                                                             └────────────────────┬─────────────────────┘
                                                                              │ validated output
                                                             ┌────────────────────▼─────────────────────┐
                                                             │ the trigger's own sink:                  │
                                                             │ IPresenceService (§3.10) · channel reply │
                                                             │ · memory tables — never run_command (§3.11)
                                                             └──────────────────────────────────────────┘
   wiring: one hosted ContextPipelineHost starts every registered trigger with the shared RunAsync callback;
   collectors are resolved per run from IEnumerable<IContextCollector>
```

- **`IContextTrigger`** — decides *when* a run starts and supplies the `ContextRequest` (feature name + scope: guild/channel/user, triggering message). Guild-scoped triggers check the per-guild opt-in (`GuildSetting`) and their cadence bounds before firing; a trigger hands the validated result to its own sink.
- **`IContextCollector`** — `Name` + `CollectAsync(ContextRequest) → ContextSlice`. Enabled per run via global `Llm:Collectors:<Name>:Enabled` (default true) plus per-guild opt-in where guild-scoped. Every slice carries a trust level: **untrusted** = channel content (FR-154: data only — never stored, logged, or executed; never triggers commands, tools, database writes, or persona/permission/config changes) · **trusted** = config, persona, database facts.
- **Persona per run** — `LlmPromptBuilder` resolves the effective persona from the `ContextRequest` scope via the §3.12 chain (guild override → global override → `Llm:Persona` → shipped seed); phase-1 global runs skip the guild layer.
- **Orchestration rules** — collector failures are isolated (skip the slice); the gate is checked before every model call; one structured log per run (trigger, feature, collectors used, outcome, tokens — never content); restart-safe by construction (nothing pipeline-specific is persisted; memory lives in its own tables, phase C).
- Phase 1 (§3.12) keeps its own loop — it is expressible as `ScheduledTrigger` + collectors, but migrating it is optional; the pipeline exists for the later stages.

Tests (planned): fake `ILlmClient` + fake triggers/collectors — registry resolution, enable/opt-in filtering, trust placement in the assembled prompt, slice-failure isolation, gate/budget interaction, per-run logging without content.

## 4. Configuration

| Area | Keys (defaults) | Notes |
|---|---|---|
| Core (`appsettings.json`, committed) | `BotConfiguration:BotName`, `LogFormat`, `CommandIdentifier` (`!`), `CooldownSecondsByTier` (missing tiers default 3 s; `0` disables; invalid = absent), `Serilog` (console + daily `logs/` file), optional `Presence`/`Bio` (§3.10) | Required keys validated at startup (`ConfigurationValidator`); missing → critical log + non-zero exit. |
| Secrets (`HambotV2/appsecrets.json`, gitignored) | `Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, optional `Mcp` | Env vars take precedence (re-added after the file); the file is `.dockerignore`d, so images are secret-free. |
| Permissions | `Permissions:BotOwnerDiscordId` (`BOT_OWNER_DISCORD_ID`) | Optional; tier 4 in every guild. |
| MCP (§3.11) | `Mcp:Enabled` (false), `Mcp:ListenUri` (`http://0.0.0.0:2525`), `Mcp:ApiKey` (required when enabled) | Compose: `MCP_ENABLED`/`MCP_API_KEY`/`MCP_LISTEN_URI`, port loopback-only. |
| Watchdog / notifications (§3.13) | `Watchdog:CheckIntervalMinutes` (5), `GatewayDownAlertMinutes` (10), `ReAlertMinutes` (60); `Notifications:ChannelId`, `MaxPostsPerMinute` (5) | Compose: `WATCHDOG_*`, `NOTIFY_CHANNEL_ID`. |
| Reminders (§3.14) | `Reminders:MinRecurrenceMinutes` (10), `MaxRecurring` (5; `0` disables `every`) | Absent/invalid → defaults. |
| Quote of the day (§3.17) | `QuoteOfTheDay:MinIntervalMinutes` (1) | Deployment-level interval floor. |
| Slash surface (§3.15) | `SlashCommands:Enabled` (false) | Pagination buttons (§3.19) are always active. |
| Backups (§5.2) | `Backups:Directory` | In-bot backup commands only; scheduling itself is compose-side env (`BACKUP_INTERVAL` 24 h, `BACKUP_RETENTION` 7, `BACKUP_TEST_INTERVAL` 30 d, `BACKUP_RCLONE_REMOTE` + S3 credentials). |
| LLM **(planned)** (§3.12) | All `Llm:*` keys — full settings table in §3.12 | Secret `Llm:ApiKey` via env (`Llm__ApiKey`); absent/invalid → defaults, absent key → features inactive. Runtime persona overrides (`!persona`, `!settings persona`) beat the `Llm:Persona` config seed. |
| Context pipeline **(planned)** (§3.20) | `Llm:Triggers:<Name>:Enabled` (true), `Llm:Collectors:<Name>:Enabled` (true); guild-scoped opt-ins and phase-B cadence bounds via `GuildSetting`/config | One key per registered trigger/collector name. |

## 5. Deployment

Containerized via `HambotV2/Dockerfile` (multi-stage .NET 10 build → runtime-only image on the ASP.NET Core base — required by the in-process MCP endpoint — plus `postgresql-client-17` for the in-bot backup commands). Build context is the repo root; the image is secret-free.

```
┌─ docker-compose.yml ──────────────────────────────────────────────────────┐
│ db      postgres:17, healthcheck, db-data volume                          │
│ bot     waits for db healthy; env secrets from .env; MCP port published   │
│         loopback-only; mounts the shared backups volume                   │
│ backup  sidecar (scripts/backup/: postgres:17-alpine + rclone) on the     │
│         backups volume (chmod 1777) → writes dumps + .backup-status       │
└───────────────────────────────────────────────────────────────────────────┘
        cp .env.example .env   # DISCORD_BOT_TOKEN, POSTGRES_PASSWORD
        docker compose up -d --build
```

Startup order: **config validation → migrations → gateway login**; any failure is a critical log line and non-zero exit. Compose defaults the connection string to the `db` service (`host.docker.internal` for plain containers).

### 5.1 CI/CD pipeline — Jenkins auto-deploy

```
push ──► Jenkinsfile: Build (Release, warnings as errors) ──► Test (dotnet test, TRX archived)
                                                            ──► tag v<major.minor.patch>? ──no──► stop (never deploy)
                                                                 │ yes
                                                                 ▼
                              Deploy: SSH to server → scripts/deploy.sh:
                              snapshot running image (rollback point) → build new image before touching
                              containers → swap only changed containers (secrets only from the server's
                              gitignored .env) → poll startup lines ("Database migrations applied
                              successfully", "Discord gateway connection established.", "Bot version: <expected>")
                              → rollback + non-zero exit if absent; else prune dangling images, write .deploy-info
```

One-time server prep: `hambot-deploy` user in the docker group, clone at `/opt/hambotv2`, `.env` filled there (the pipeline never touches it), SSH key credential in Jenkins (`DEPLOY_HOST/USER/DIR/CREDENTIAL`, SSH Agent plugin). Full guide: [`jenkins-cicd.md`](jenkins-cicd.md).

### 5.2 Database backups

**Sidecar loop** (`scripts/backup/run-backup.sh`): `pg_dump -Fc` → `backups` volume → validate (`pg_restore --list` + size floor; invalid dump deleted, counts for nothing) → prune beyond retention (only after a good dump) → rclone offsite push when configured (failed push is logged, harmless) → write `.backup-status` → sleep. Every `BACKUP_TEST_INTERVAL` (default monthly, and immediately on a fresh deployment): restore the newest dump into a scratch database, verify, drop, record. Runs even while the bot is down; credentials only from `.env`.

**Restore** — `scripts/restore-backup.sh <dump-file>` is authoritative: stop bot → recreate database → `pg_restore` → restart → verify startup lines.

**In-bot commands** (`Commands/BackupCommands/`, all tier 4, DM-usable; runtime image carries pg tools): `!backup` (on-demand dump + name/size reply), `!backups` (newest first, ≤ 10), `!restorebackup <name>` → confirm. `ProcessBackupManager` (behind `IBackupManager`) converts the Npgsql connection string to a `postgresql://` URI. A restore enters **maintenance mode** (FR-98): a singleton flag that declines every other command (and MCP `run_command`) and pauses the delivery engine and quote-of-the-day loop so nothing is marked failed; pooled connections cleared before, guild-settings cache invalidated after.

### 5.3 Weekly dependency security scan

`Jenkinsfile.security` — separate scheduled job (cron `0 4 * * 0`), deliberately not the main job and holding **no credentials**, so a scan can never reach production:

```
Build (warnings as errors, no tests) ──► SBOM (pinned dotnet CycloneDX per project + pinned jq)
  ──► Image (docker build + docker save the deployment image — never pushed or started)
  ──► Scan (Trivy over SBOMs + saved tarball, JSON, --exit-code 0: findings never fail a stage;
       tool/DB failures fail the build)
  ──► Report (jq severity summary; HIGH/CRITICAL in the deployed tree — HambotV2 SBOM + image,
       BotTests excluded — mark the build UNSTABLE, never FAILED); artifacts under security/ archived (~26 builds)
```

The Jenkins container needs the host Docker socket + CLI mounted for the Image stage (same accepted trade-off as §5.1).

## 6. Testing

- `BotTests/` (MSTest + Moq) runs without a database or token: Moq doubles the Discord message surface; command/permission tests run against EF Core InMemory through `IDatabaseContext` (isolated database per test); time-sensitive logic uses `FakeTimeProvider`.
- Coverage: each design section names its covering suites in its `Tests:` line. Cross-cutting contracts are asserted once: the registration contract in `TestCommandInfrastructure`, pipeline outcomes + audit in `TestCommandHandler`, startup config validation in `TestConfigurationValidator`.
- Build and run: `dotnet build HambotV2.sln` / `dotnet test HambotV2.sln`. The build is warning-free; CI treats warnings as errors.

## 7. Conventions

- C# with nullable reference types and implicit usings; bot-internal types are `internal` (tests rely on `InternalsVisibleTo`, which also grants Moq's proxy assembly).
- Command names must be lowercase.
- DI lifetimes: `CommandService` singleton; `CommandHandler`, `PermissionsService`, `DatabaseContext` transient — the handler is resolved per message and disposed after use.

## 8. Known Design Considerations / Current Limitations

- CI/CD: the `Jenkinsfile` builds, tests, and auto-deploys version tags only; ordinary pushes (including `master`) never deploy. A second scheduled pipeline (`Jenkinsfile.security`, §5.3) runs the weekly dependency scan and never deploys.
