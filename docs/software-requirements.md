# Software Requirements

The requirements for HambotV2 — the software as it *should be*, traceable when the system changes. Sections 2.11 onward are the post-1.0.0 features (2.27 is reserved for roadmap Phase B2); the unimplemented ones are the outstanding [`roadmap.md`](roadmap.md) backlog.

## 1. Product Description

HamBot is a Discord chat bot. It recognizes messages starting with a configurable command identifier (default `!`) in its guilds and executes registered commands, governed by a tiered permission system persisted in PostgreSQL and scoped per guild.

## 2. Functional Requirements

### 2.1 Discord connectivity

- **FR-1**: Connect to the Discord gateway with the token from `Secrets:BotToken` (never committed, NFR-3).
- **FR-2**: Request only the intents the bot uses (`Guilds | GuildMessages | DirectMessages | MessageContent`), not `AllUnprivileged`.
- **FR-3**: Bridge gateway log events via `BotConfiguration:LogFormat`.
- **FR-4**: Log gateway state every 5 minutes.
- **FR-5**: Run continuously as a background worker until cancelled.
- **FR-6**: Log out and dispose the client cleanly on shutdown; log disconnects/reconnects.

### 2.2 Command recognition

- **FR-7**: A message starting with its guild's effective command identifier (per-guild override, global default `BotConfiguration:CommandIdentifier`; DMs use the global default) is a command invocation.
- **FR-8**: The command name is the first space-delimited token, identifier stripped, lowercased.
- **FR-9**: Degenerate invocations (bare identifier, whitespace-only, empty tokens) are handled safely.
- **FR-10**: An unregistered name is checked against the guild's custom responses (FR-54) before the "unavailable" reply.
- **FR-11**: Commands are looked up by name; names must be lowercase (test-enforced).

### 2.3 Command execution & error handling

- **FR-12**: Commands implement `ICommand` and register through the registration chain, carrying their own help text (FR-39).
- **FR-13**: A command receives an execution context wrapping a per-message database context.
- **FR-14**: Insufficient permissions → in-channel denial, no execution.
- **FR-15**: Failures produce a user-facing in-channel error plus a logged exception, never stop message processing, and never expose exception details.
- **FR-16**: Per-message resources are always released, including on failure.

### 2.4 Permissions (per guild)

- **FR-17**: Tier-based access: a user may run a command iff their tier ≥ its required tier, within the invoking guild.
- **FR-18**: Users without a database row default to tier 0.
- **FR-19**: Commands without a `CommandMetaData` row default to required tier 0.
- **FR-20**: Tiers are stored per guild, per user and per command, in PostgreSQL.
- **FR-21**: Tiers are adjustable at runtime, no restart, via `!commandpermission <name> [tier]` and `!userpermission <user> [tier]` (one arg reports, two args set).
- **FR-22**: `!userpermission` accepts `<@id>`/`<@!id>` mentions and raw ids.
- **FR-23**: The permission-management commands are themselves permission-checked (their tiers can be raised).
- **FR-24**: Multiple guilds are supported with all persisted state scoped per guild.
- **FR-25**: **DM scope**: in direct messages the user acts at owner tier (the configured bot owner at tier 4), commands require their built-in defaults, no per-guild state is read or written, and guild-state commands decline in-channel.
- **FR-26**: Bot configuration (e.g. command identifier) is configurable per guild, with global values as defaults.
- **FR-33**: The tier scale is named and documented (0 everyone, 1 moderator, 2 admin, 3 owner).
- **FR-34**: The guild owner automatically holds owner tier in their guild — the bootstrap administrator, no database setup needed.
- **FR-35**: `!commandpermission` and `!userpermission` default to admin tier, so unprivileged users cannot self-promote on a fresh guild.
- **FR-36** (escalation guard): Users may only assign tiers strictly below their own, and never modify their own or an equal/higher-tier user's tier.
- **FR-37**: Assigned tiers are validated against the scale; invalid input is an in-channel error.
- **FR-38**: `!help` lists the commands the invoking user can run, grouped by category (FR-132), alphabetized within groups, paginated at 10 per page with a character-budget guard and group-aware page breaks (FR-133).
- **FR-39**: Every command supplies its description and usage with the implementation.
- **FR-40**: `!help <name>` shows detailed help only for runnable commands; unknown and insufficient-tier names get the same generic reply, never revealing higher-tier commands.
- **FR-41**: A bot owner id (`Permissions:BotOwnerDiscordId`) holds tier 4 in every guild — configuration-only, never assignable via commands — and is the cross-guild authority.

### 2.5 Persistence & startup

- **FR-27/FR-28**: User tiers and command metadata persist across restarts via EF Core.
- **FR-29**: The connection string comes from `ConnectionStrings:DefaultConnection` (never committed).
- **FR-30**: EF Core migrations are applied automatically on startup.
- **FR-31**: An unreachable database at startup fails fast with a non-zero exit code.
- **FR-32**: Missing/invalid required config keys (`Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, `BotConfiguration:BotName`, `LogFormat`, `CommandIdentifier`) are detected at startup, naming the offending key.

### 2.6 Quotes (per guild)

- **FR-42**: Quotes are stored per guild (`Quote` entity: text, adder id, timestamp); never visible, searchable, or removable in another guild.
- **FR-43**: Per-guild sequential numbering, **0-based like the old bot** (`MAX(Number) + 1`); removal never renumbers.
- **FR-44**: `!quote` replies with a random guild quote, number-prefixed; empty guild → friendly "no quotes yet".
- **FR-45**: `!quote <number>` fetches that quote; unknown numbers are in-channel errors; non-numeric arguments are treated as text to add (FR-47).
- **FR-46**: `!searchquotes <text...>` — case-insensitive substring match, plus word-level near misses ranked behind the substring hits (FR-150); number-prefixed, results paginated most-relevant-first (FR-152, FR-153); no matches → friendly reply; empty term → in-channel error.
- **FR-47**: `!quote <text...>` adds the text (≤ 2000 chars, longer rejected) and replies with the number; over-limit replies are trimmed with an ellipsis, stored text never modified.
- **FR-48**: `!removequote <number>` removes and confirms; tier moderator. `quote` and `searchquotes` default to tier 0.
- **FR-49**: Quote commands are subject to the standard pipeline behavior (tier adjustment, cooldowns, shared argument parsing, help text, lowercase names).
- **FR-50**: Quote commands decline in DMs ("only available in servers").

### 2.7 Custom command responses (per guild)

- **FR-51**: Per-guild `CustomResponse` entity (name, content, last modifier, timestamps); scoped per guild.
- **FR-52**: Names: one lowercase token, 1–32 chars, `[a-z0-9_-]`; invalid names rejected in-channel.
- **FR-53**: A response can never shadow a built-in command, including built-ins added later.
- **FR-54**: On an unknown token, custom responses are consulted before the "unavailable" reply; a match replies with the stored content.
- **FR-55**: Custom response invocations go through the same checks as built-ins (cooldown keyed on the name; permission tier default 0, adjustable via `!commandpermission`).
- **FR-56**: `!addresponse <name> <content...>` creates or updates (reply distinguishes), moderator tier; empty content rejected; content ≤ 1000 chars.
- **FR-57**: `!removeresponse <name>` deletes and confirms, moderator tier.
- **FR-58**: `!listresponses` lists names alphabetically, tier 0.
- **FR-59**: Custom responses and their management commands are guild-only; DMs keep the plain "unavailable" reply.
- **FR-60**: Content is text only: attachments on `!addresponse` are rejected; dispatched responses never upload files; links are stored and sent verbatim (Discord renders previews).

### 2.8 Reminders (user-scoped)

- **FR-61**: Reminders are per-user (`Reminder` entity: message, UTC due time, owner id, guild id of creation — null in DMs) and work from guilds and DMs.
- **FR-62**: `!remindme <duration> <message...>` parses leading number+unit tokens (`s`/`m`/`h`/`d`/`w`, long forms, compact and spaced, summed); message is the remainder, ≤ 500 chars.
- **FR-63**: The duration must be positive — no minimum or maximum; unparsable/non-positive input is an in-channel error, nothing stored.
- **FR-64**: Success confirms in-channel, echoing message and due time.
- **FR-65**: A hosted service delivers due reminders as owner DMs, second-accurate (wake at the earliest pending time, re-schedule on changes, 10-second fallback poll); delivered reminders leave the pending set; delivery never blocks or crashes the service.
- **FR-66**: Reminders survive restarts; one due during downtime is delivered on start.
- **FR-67**: Delivery failures retry a bounded number of times, then are marked failed and logged; never block other reminders.
- **FR-68**: `!reminders` lists the invoker's pending reminders soonest first, ≤ 10.
- **FR-69**: `!cancelreminder <number>` cancels one of the invoker's own; users can never see or cancel others'.
- **FR-70**: Per-user cap of 10 pending reminders, enforced with an in-channel error.
- **FR-71**: The reminder commands are tier 0 and fully usable in DMs.

### 2.9 Quote import (legacy migration)

- **FR-72**: `!importquotes`, run by the bot owner in the target guild with the old bot's `quoteSettings.json` attached (a `quotes` array of `{ quote, date? }`), downloads and parses the file.
- **FR-73**: Entries append in file order, continuing the numbering; `AddedByDiscordId` is the importer; `CreatedAt` takes the legacy `date` when present.
- **FR-74**: The import is all-or-nothing: any malformed file, empty list, or invalid entry (empty or > 2000 chars) is an in-channel error naming the problem, storing nothing.
- **FR-75**: Default tier 4 (configuration-only) — only the configured bot owner can ever run it; declines in DMs.

### 2.10 Version reporting

- **FR-76**: Every build embeds a version derived from git: latest `v`-prefixed tag semver + 7-char commit hash as build metadata (`v1.2.3` @ `1639324…` → `1.2.3+1639324`); untagged → `0.0.0+<hash>`; no git → `0.0.0+unknown`; never misidentifies unreleased code as released.
- **FR-77**: `!version` replies with the version; tier 0, DM-usable, help text like every command.

### 2.11 Discord presence & profile identity

- **FR-78**: A single presence service handles all presence changes (startup config, `!presence`, the LLM job, the MCP tool). Optional `BotConfiguration:Presence:Type`/`:Text` (four types, ≤ 128 chars) applies once the gateway is ready; absent → no activity; invalid → named-key startup failure.
- **FR-79**: `!presence` shows, sets, or clears the activity; tier bot owner; DM-usable; runtime changes not persisted.
- **FR-80**: Concurrent presence updates are serialized, last request wins; apply failures are logged, surfaced as in-channel errors from commands, and never crash the bot.
- **FR-113**: The bio is managed through the same service. Optional `BotConfiguration:Bio` (≤ 190 chars, line breaks allowed) applies per member guild once ready; absent → untouched. `!bio` shows/sets/clears; tier bot owner; DM-usable; not persisted.
- **FR-115**: `!avatar` shows the current URL, or sets the picture from exactly one image attachment or a URL (validated: real image, within Discord's limits), applied per guild plus the global avatar; `!avatar reset` falls back to the application default. Tier bot owner; DM-usable; no startup config, not persisted.
- **FR-116**: `!banner` — same shape as `!avatar` but per guild only; `!banner clear` removes it.

### 2.12 Database backups

- **FR-81**: A dedicated compose `backup` service dumps the database on a configurable schedule (default daily) as timestamped `pg_dump -Fc` files; retention (default 7) prunes only after a new dump succeeds.
- **FR-82**: After each dump, push offsite to configurable S3-compatible storage; optional (skipped without a remote); a failed push is logged and harmless.
- **FR-83**: `scripts/restore-backup.sh <dump-file>` restores a dump: stop the bot, restore into a recreated database, restart, verify the startup lines; documented and proven once in a drill (NFR-19).
- **FR-84**: Backups run independently of the bot process.
- **FR-85**: Backup credentials come exclusively from the gitignored `.env`; dumps contain database content only.
- **FR-96**: `!backup` triggers an on-demand dump (same format and directory) and replies with name and size; tier bot owner; DM-usable; failures leave the schedule unaffected.
- **FR-97**: `!backups` lists the dumps newest first, ≤ 10 entries; friendly message when empty.
- **FR-98**: `!restorebackup <name>` is a two-step confirmed restore; while it runs the bot is in **maintenance mode** (other commands, MCP `run_command`, and scheduled deliveries are deferred — nothing marked failed due to a restore); guild-settings caches are invalidated after. Tier bot owner; DM-usable. The script (FR-83) remains authoritative for a broken bot.
- **FR-100**: Every dump is validated before it counts (`pg_restore --list` + size floor); invalid dumps are deleted, don't count for retention, aren't pushed, and alert the watchdog.
- **FR-101**: An automated restore test on a configurable schedule (default monthly) proves restorability against a scratch database, recording the outcome for `!health` and the watchdog.

### 2.13 MCP server (operator-agent integration)

- **FR-86**: An opt-in MCP server over streamable HTTP (`Mcp:Enabled` + `Mcp:ApiKey`, default disabled); the startup log names its state; not reachable off the deployment host (loopback-only publish).
- **FR-87**: Every request must carry the bearer API key; anything else is 401 before any tool logic.
- **FR-88**: Status/identity tools: `bot_status`, `list_guilds`, `list_commands`, `set_presence`, `set_bio`.
- **FR-89**: Messaging tools: `list_channels(guildId)`, `get_recent_messages(channelId, limit ≤ 50)`, `send_message(channelId, text ≤ 2000, plain text)`.
- **FR-90**: `run_command(guildId, commandLine)` executes through the real command pipeline with identical semantics; the reply is captured and returned as the tool result, never posted to a channel.
- **FR-91**: Each tool call logs one structured event (tool, outcome, no content); failures become error results and never crash the endpoint or bot.
- **FR-120**: A per-guild operator-agent tier (`GuildSetting.OperatorAgentTier`, default the lowest tier) sets the virtual user's authority; commands above it are denied normally. Managed by `!agenttier` (owner tier, guild-only, declines in DMs); an agent invocation of the command itself is declined — the agent can never raise its own ceiling. Cached per guild and invalidated after a restore.
- **FR-139**: A member-lookup tool `get_user_profile(guildId, userId)` — a read-only overview of one specific member: Discord identity (username/display name, nickname, roles, join date, degraded gracefully when the member is no longer resolvable) plus the bot's records for them in that guild: permission tier (default 0), active bot-ban record (expired ones lift on sight, FR-137), quote count with the most recent quote, and last-modified custom responses. Reminders are excluded — they are the member's private state (FR-61).
- **FR-140**: `get_user_quotes(guildId, userId)` — the quotes that member added in the guild, newest first, number-prefixed like the `!quote` reply (FR-44), capped at 50 entries with a "+N more" count.
- **FR-141**: `get_user_messages(guildId, userId, limit ≤ 50)` — the member's most recent messages across the guild's bot-readable channels, newest first, filtered by author; content appears in the result but never in routine logs (FR-91).
- **FR-143**: `search_users(guildId, query, limit ≤ 25)` — a case-insensitive member search over username, display name, and server nickname, resolving a name the agent encountered (a message author, "who is X") to a user id for the other lookup tools; backed by Discord's REST member search within the bot's existing guild access, its candidate set ranked closest-first locally (FR-151). A missing query is an error; members who left are not findable (current members only).
- **FR-142**: The lookup tools (FR-139–FR-143) are read-only: nothing is written, executed, or posted, so the operator-agent tier (FR-120) does not gate them; they share the endpoint's authentication (FR-87), tracing, and error semantics, and read only what the bot already holds or can already read (no new gateway intents, no external data enrichment). During maintenance mode (FR-98) they decline with the same captured notice as `run_command`. The bot compiles and stores nothing — interpreting the records is the calling agent's job.

### 2.14 LLM integration (presence & bio updates)

- **FR-92**: A Mistral LLM client configured via `Llm:ApiKey` (a secret, NFR-3) and `Llm:Model` (default `mistral-small-latest`); no key → all LLM features inactive.
- **FR-93**: An opt-in presence-update job (`Llm:PresenceUpdates:Enabled`, interval `Llm:PresenceUpdates:IntervalMinutes` default 60 min): each tick builds the prompt (FR-154), validates the output (non-empty, single line, ≤ 128 chars), and applies it via the presence service.
- **FR-94**: LLM/API errors or invalid output leave the current presence/bio unchanged, are logged, retry next interval, and never crash the bot.
- **FR-95**: The LLM client is an interface with a Mistral HTTP implementation, so prompts/validation/scheduling are unit-testable without network.
- **FR-99**: A documented, configuration-overridable default persona seed ships with the bot; authoring and tuning it is part of the feature work.
- **FR-102**: Usage is accounted per feature per UTC day (requests, tokens in/out) and exposed to `!llmstatus` and `!health`; in-memory.
- **FR-103**: A daily request budget (`Llm:DailyRequestBudget`, default 500) hard-stops API calls for the rest of the UTC day; `0` disables the feature set like an absent key.
- **FR-104**: `!llmswitch on|off` (tier bot owner, DM-usable, slash-eligible; the LLM command surface is FR-168) sets/clears the in-memory kill switch — while on, the gate refuses every LLM call; a restart restores the configured state.
- **FR-114**: With `Llm:BioUpdates:Enabled`, a bio-refresh job runs on its own interval (`Llm:BioUpdates:IntervalMinutes`, default 60 min, floor 1 min — independent of the presence interval) — same prompt inputs (FR-154), non-empty, ≤ 190 chars, line breaks allowed — with identical validation, failure handling, and budget accounting.
- **FR-154**: The presence/bio prompt inputs are the persona seed (FR-99), the bot's name and version, and the current UTC date/time. Chat context is per-guild opt-in (`!llmcontext on`, persisted per guild, default off): an LLM update triggered from an opted-in guild includes recent chat from that guild's readable channels only, and automatic (scheduled) runs include chat from every opted-in guild — bounded (configurable, defaults 20 messages and 4000 characters total); DM-triggered runs read no chat, and DMs are never read. Chat content is untrusted input — data, never instructions: it is never stored, logged, or executed, never triggers commands, tools, database writes, or persona/config changes, and the prompt requires generic output (no quoting, no user or channel names); secrets (NFR-3) are never prompt input. Recency weighting in the chat block: messages are ordered newest-first and both caps (`MaxMessages`, `MaxCharacters`) consume newest-first — when a cap bites, the oldest messages are dropped first, whole lines only, never mid-line — and the prompt's chat block states the newest-first ordering and instructs the model to treat more recent messages as more relevant to the current conversation. Messages older than 24 hours — configurable via `Llm:ChatContext:MaxAgeHours` (default 24), evaluated against UTC now — are dropped entirely before the caps; a channel whose newest message is that old contributes nothing.

- **FR-155**: `!persona global` (tier admin, DM-usable, slash-eligible) shows, sets (`!persona global <text...>`), and clears (`!persona global clear`) the global runtime persona override, persisted in global storage; the fallback chain is the `Llm:Persona` config value → the shipped default seed (FR-99); the override survives restarts. Persona text is non-empty and ≤ 2000 chars.
- **FR-156**: `!persona server <text...>|clear` (tier admin, guild-only, slash-eligible) sets/clears a per-guild persona override persisted in the guild's settings. Persona resolution for an LLM run: guild-scoped run with a guild override → guild override; else global runtime override (FR-155) → `Llm:Persona` → shipped seed. The presence/bio job is global and uses the global chain only.
- **FR-161**: `!model` (tier admin, DM-usable, slash-eligible) shows the effective LLM model and which layer supplied it, `!model <name>` sets the global runtime model override persisted in global storage — validated against the provider's model list, never stored when unknown — and `!model clear` removes it. Model resolution: runtime override → `Llm:Model` configuration → shipped default; the override applies from the next model call without a restart.
- **FR-162**: `!model list` lists the provider's available model ids (fetched live from the Mistral API through the same gate guardrails: kill switch, daily budget, accounting; a blocked or failed call yields an in-channel error and changes nothing). Without a configured API key the LLM surface reports inactive.
- **FR-163**: `!llmupdate [presence|bio]` (tier bot owner, DM-usable, slash-eligible) immediately runs an LLM update for the given surface — both when the argument is omitted — regardless of the scheduled-job flags (`Llm:PresenceUpdates:Enabled` / `Llm:BioUpdates:Enabled`); the call still goes through the gate (kill switch, daily budget, per-feature accounting), scheduled and manual runs never overlap, and the reply reports the per-surface outcome: applied / inactive (no key or zero budget) / blocked (kill switch or exhausted budget) / failed (call error, counted toward the consecutive-failure alert) / invalid output (current state kept). The update service stays alive while active even with no job enabled, so manual updates work; with no key the surface reports inactive.

- **FR-164**: A per-guild allowlist of channels the LLM chat context may read — a new per-guild table `GuildLlmChannel` (key `(GuildId, ChannelId)`); an empty list means all readable text channels, the current FR-154 behavior. The allowlist only narrows FR-154: unlisted channels are never read, in guild-triggered or automatic runs; DMs are still never read; the `!llmcontext on` opt-in still gates the guild as a whole — a non-opted-in guild contributes no chat regardless of its allowlist rows.
- **FR-165**: `!llmchannels` (tier admin, guild-only, slash-eligible): no args show the allowlist (or "all readable channels" when empty); `add <#channel|id>` / `remove <#channel|id>` modify it (the channel must be a text channel of this guild — raw id or `<#id>` mention; adding an already-listed channel or removing an unlisted one is a no-op with a friendly reply); `clear` empties the list, restoring all-readable behavior. Changes apply to the next LLM run, no restart.
- **FR-166**: `ChatContextCollector` receives the per-guild allowlists together with the run scope and reads only allowed channels — still skipping unreadable ones, so an allowed but unreadable channel never fails the run; the allowlists are resolved alongside the `LlmIncludeChat` opt-in when the run scope is built.

- **FR-167**: `!llminterval <presence|bio> [minutes|reset]` (tier bot owner, DM-usable, slash-eligible) sets one update job's interval at runtime — whole minutes, floor 1; `reset` restores the configured value. The override is in-memory (a restart restores the configured state), applies from the next tick (the loops read the current interval every tick), and `!llmstatus` shows the effective interval, marked as a runtime override while one is active.

- **FR-168**: LLM settings are eight separate top-level commands (all slash-eligible administrative commands, FR-110) — no unified `!llm` command with subcommands. Bot-owner tier (4), DM-usable: `!llmstatus` (status report — active/inactive, effective model + layer, kill switch, job states with intervals, per-feature usage, budget used/limit, consecutive failures), `!llmswitch [on|off]` (kill switch; no args shows the state; FR-104), `!llmupdate [presence|bio]` (manual update, FR-163), `!llminterval <presence|bio> [minutes|reset]` (per-job runtime interval, FR-167). Admin tier (2): `!model [list|<name>|clear]` (FR-161/162, DM-usable), `!persona` (no args shows the effective persona for the current scope — in a guild the server override if set plus the global chain, in DMs the global chain; `global [<text...>|clear]` DM-usable, FR-155; `server [<text...>|clear]` guild-only, FR-156), `!llmcontext [on|off]` (guild-only, FR-154), `!llmchannels [add|remove <#channel|id>|clear]` (guild-only, FR-165). Each command declares its own default tier, enforced by the pipeline (FR-17) — no in-command guard is needed, so a `!commandpermission` override on one command never exposes another command's tier. `!settings` keeps only `commandidentifier` and `cooldown`. Without a configured API key the gate-dependent commands (`llmstatus`/`llmswitch`/`llmupdate`/`llminterval`/`model`) report the features inactive, while `persona`/`llmcontext`/`llmchannels` still work.

- **FR-169**: The previous presence/bio text is never a prompt input (amends FR-154's input list): feeding the current surface to the model makes it reproduce it verbatim, so updates would leave the surface unchanged. Presence/bio prompt inputs are the persona + bot name/version + current UTC date/time + optional chat context only. The current activity *type* is still reused when applying a new presence — a setting, not prompt input.

### 2.15 Watchdog, health reporting & operator notifications

- **FR-105**: `!health` reports version, uptime, gateway state, database reachability, backup/restore-test outcomes, LLM state (active/inactive, kill switch, budget used, consecutive failures), MCP state, pending reminder count, and the notification channel. Tier bot owner; DM-usable.
- **FR-106**: A watchdog loop (default 5 min) alerts the bot owner by DM on: gateway down past a threshold (default 10 min); backup validation failure, overdue backup, or failed/overdue restore test; LLM budget exhaustion or three consecutive LLM failures; maintenance mode. Each condition alerts once, re-alerts at most hourly, announces recovery once; also posted to the notification channel; log-only without a configured owner; checks never crash the bot.
- **FR-117**: An operator notification channel: configurable default (`Notifications:ChannelId`), runtime-managed via `!notifychannel` (show / `<channel mention or id>` / `clear`) persisting in global storage; tier bot owner; DM-usable.
- **FR-118**: With a channel set, watchdog alerts/recoveries, command failures, retry-exhausted reminders, maintenance/restore outcomes, and notification delivery failures are posted there, rate-limited (default 5/min) with identical consecutive messages collapsed; failed posts never affect the originating feature; startup criticals stay log-only.

### 2.16 Recurring reminders

- **FR-107**: `!remindme every <duration> <message...>` creates a recurring reminder: first due after the interval, re-scheduled from the actual delivery time each delivery (downtime shifts the schedule, no catch-up burst); interval floor configurable (default 10 min).
- **FR-108**: Recurring reminders keep their number across deliveries; `!reminders` marks them; `!cancelreminder` cancels permanently; retry exhaustion stops the recurrence and marks it failed.
- **FR-109**: A separate per-user recurring cap (default 5), never consuming one-shot slots.

### 2.17 Discord application commands (slash commands)

- **FR-110**: Opt-in (`SlashCommands:Enabled`, default false). Eligible: administrative/configuration commands, the user-scoped reminder commands, the read-only `!searchquotes` (as `/searchquotes` with a required `text` option), `!version` (as `/version`), and `!help` (as `/help`); the remaining quote and custom-response commands stay text-only. Eligibility is declared per command; definitions are built from the same registry and bulk-overwritten idempotently on startup; custom responses are not registered.
- **FR-111**: A slash invocation runs through the same pipeline as text: identical tiers, shared cooldown keys, the same audit event and error semantics; DM interactions run in the DM scope.
- **FR-112**: All slash replies are ephemeral (visible only to the invoker), text-identical to the text surface; the text surface stays unchanged and public.
- **FR-119**: A multi-page help list carries invoker-owned previous/next buttons on both surfaces; foreign clicks are declined ephemerally without running the pipeline; the invoker's click executes as a `help <page>` invocation with full pipeline semantics and updates the message in place; button handling is active regardless of `SlashCommands:Enabled`.

### 2.18 Dependency security scan (weekly Jenkins job)

- **FR-121**: A weekly scheduled Jenkins job runs the scan non-interactively; it never deploys and holds no deploy or bot credentials.
- **FR-122**: A CycloneDX SBOM per project covers every direct and transitive NuGet package; the `HambotV2` SBOM is the authoritative deployed-image inventory.
- **FR-123**: The scan also builds and scans the deployment image (runtime base layers and apt packages); the image is never pushed or started.
- **FR-124**: A non-interactive scanner checks every SBOM and the image for CVEs, producing one machine-readable report per input.
- **FR-125**: SBOMs and reports are archived per build with retention; the output includes a human-readable high/critical summary.
- **FR-126**: Informational, not gating: low/medium never affect the result; high/critical in the deployed tree mark the build UNSTABLE (never FAILED); `BotTests` findings never affect the result.
- **FR-214**: A Jenkins job (`Jenkinsfile.security-fix`) triggered by the completion of the FR-121 scan job (upstream trigger, threshold UNSTABLE — every completed scan, never a failed one) consumes the last scan build's archived reports and, without user intervention, opens a pull request that bumps every CRITICAL finding's direct `PackageReference` to the highest published fixed version within the installed package major — but only after a green build+test on the fix branch. Findings that cannot be auto-fixed (no published fix, transitive-only package, cross-major fix, image/apt layer) are listed in the PR body as manual notes. The job never merges the PR, never deploys, and holds no deploy or bot credentials.

### 2.19 Quote of the day (scheduled publication)

- **FR-127**: Per-guild configuration on the guild's settings: target channel (null = off — the only activation switch), template (default `quote of the day: {quote}`), interval (default 24 h), and anchor (default 00:00 UTC). Entirely UTC, no per-guild time zones; configurable deployment-level interval floor (default 1 min).
- **FR-128**: `!quoteoftheday` (admin tier, guild-only, slash-eligible): no args report the config or "off"; `channel <#ch|id>` / `channel clear` activate/deactivate; `template <text...>` (non-empty, ≤ 500 chars, contains `{quote}`); `<due time> <interval>` sets the schedule (due time is a single duration, its end becomes the anchor; interval must be whole minutes ≥ the floor). Schedule changes take effect at the next boundary of the new schedule, never mid-boundary; channel/template changes leave the schedule untouched.
- **FR-129**: On a boundary the bot publishes one random quote of the guild through the template (capped at Discord's limit with an ellipsis); a guild with no quotes skips; maintenance mode pauses publications with no catch-up burst.
- **FR-130**: Fixed wall-clock boundaries (anchor + whole multiples of the interval, UTC) — same time every day, no drift from late/failed posts; boundaries passed while down or off are skipped, at most one publication per boundary; the next due time is derived from configuration.
- **FR-131**: A failed post is logged, skips its boundary, and leaves the schedule and the feature unchanged; no upfront channel validation; no new gateway intents.

### 2.20 Help command grouping (readability)

- **FR-132**: Every command declares one category from a fixed code-defined set (General, Quotes, Custom responses, Reminders, Permissions & settings, Moderation, Backups & stability, Bot profile, LLM); undefined categories are rejected at registration. Presentation-only.
- **FR-133**: Grouping never leaks: only commands the invoker can see are rendered, empty groups are omitted, and both help surfaces render identically. Pagination stays group-aware: a group that fits a page is never split; an oversized group repeats its header on continuation pages.

### 2.21 User bot bans (moderation)

- **FR-134**: Per-guild `BotBan` entity (guild + user id, banning user, creation, nullable expiry — null = permanent); guild-scoped (DMs never blocked); re-banning updates the record.
- **FR-135**: Enforcement at the very front of the pipeline, on both surfaces: an active ban drops every invocation silently — no reply of any kind — audited with outcome `Banned`.
- **FR-136**: The guild owner and bot owner can never be banned and bypass the check; the invoker cannot ban themselves; operator-agent invocations bypass the check.
- **FR-137**: Temporary bans lift lazily at read time — an expired record is deleted on sight; no scheduled job.
- **FR-138**: Server-scoped moderator-tier commands (declining in DMs, Moderation category, slash-eligible): `!banuser <user> [duration]` (reminder duration forms; escalation guard applies; invalid input stores nothing), `!unbanuser <user>` (deliberately unguarded), `!banlist` (soonest expiry first, ≤ 10 entries, expired lifted on sight).

### 2.22 Unknown-command suggestions ("did you mean?")

- **FR-144**: When an invocation's name resolves to neither a built-in command (FR-11) nor, in a guild, a custom response (FR-54), the pipeline computes at most one suggestion before sending the "unavailable" reply. Exact-name matches never reach the suggestion path — built-ins and custom responses win by construction (FR-53, FR-54). Applies to the text surface and MCP `run_command` only; the slash surface is excluded — Discord's own autocomplete already guides users there, and unknown names cannot be invoked on it at all.
- **FR-145**: Candidates are all registered built-in command names plus, in a guild, the invoking guild's custom response names — they dispatch from the same path, so a typo of one is as dead an end as a typo of a built-in. DMs consider built-ins only (FR-59). A custom response is suggested by its bare name, like a command.
- **FR-146**: Matching is case-insensitive. A candidate qualifies iff its Levenshtein edit distance from the attempted token is ≤ 2, or the candidate begins with the token and the token is ≥ 3 characters (so `rem` finds `remindme`). The single best qualifying candidate is suggested — lowest distance wins, ties broken alphabetically. Nothing qualifies → the existing reply, unchanged.
- **FR-147**: Suggestions never leak commands the invoker cannot run: candidates whose effective required tier exceeds the invoker's effective tier are excluded before matching (per-guild tiers in a guild, FR-17; built-in defaults in DMs, FR-25; the operator-agent grant for virtual invocations, FR-120) — mirroring FR-40's never-reveal rule. Banned users never get a suggestion (FR-135 drops them before this path).
- **FR-148**: The suggestion is one extra line appended to the "unavailable" reply, e.g. `Did you mean !quote?`, using the invocation's effective command identifier (FR-7). It is part of the channel reply itself, so virtual invocations capture it with no special handling; the audit event stays `UnknownCommand`, no content logged.

### 2.23 Fuzzy search — typo tolerance via the shared matcher

- **FR-149**: The fuzzy-matching logic (the FR-146 algorithm: case-insensitive Levenshtein distance with a per-consumer qualification rule) is one shared pure matcher. The suggestion path (FR-144–FR-148), `!searchquotes` (FR-150), `search_users` (FR-151), and the participation trigger's name mention (FR-172) all use it — no per-feature fuzzy implementations.
- **FR-150**: `!searchquotes <text...>` matches a quote iff it contains the term case-insensitively (unchanged, FR-46) or one of its whitespace-delimited words is within edit distance ≤ 2 of the whole term. Substring matches rank ahead of fuzzy matches; fuzzy matches rank by distance, ties by quote number ascending. The no-match/empty-term replies are unchanged.
- **FR-151**: `search_users(guildId, query, limit ≤ 25)` keeps its Discord REST candidate set (current members only, FR-143) but ranks it locally with the matcher: distance of the query to the closest of username, display name, and nickname — lowest first, ties broken alphabetically — so the closest name leads. The cap and error semantics are unchanged.

### 2.24 Quote search pagination (result backlog)

- **FR-152**: A search reply holds one page of 10 ranked matches with a header naming the term, the page, and the relevance cap; when more than one page exists, the reply carries invoker-owned previous/next buttons. A search that fits one page has no buttons.
- **FR-153**: The backlog keeps each invoker's latest search snapshot — up to the 100 most relevant matches, already ranked — so clicks render pages without re-querying or re-ranking. A newer search replaces the invoker's entry; buttons of a replaced or lost search (e.g. after a restart) get an ephemeral run-it-again reply, and a foreign clicker is declined ephemerally without touching the message.

### 2.25 Context pipeline — registered triggers and collectors *(planned)*

- **FR-157**: LLM runs are started only by registered triggers (`IContextTrigger`, resolved as `IEnumerable<IContextTrigger>`; one hosted `ContextPipelineHost` starts them all with a shared run callback). Trigger kinds: scheduled (interval on the injected `TimeProvider`), message-event (a new message in a per-guild opted-in guild, cadence-bounded), and on-demand (a command or MCP invocation). A trigger decides only *when* a run starts and supplies the request scope (feature name, guild/channel/user, triggering message); it never builds prompts or calls the model.
- **FR-158**: Context is supplied by registered collectors (`IContextCollector`: `Name` + `CollectAsync(ContextRequest)` returning a named, trust-marked slice), resolved per run from `IEnumerable<IContextCollector>` and filtered by `Llm:Collectors:<Name>:Enabled` (default true) plus per-guild opt-in where guild-scoped. `ChatContextCollector` (FR-154) is the first collector; adding another is one implementation plus one DI registration, with no orchestrator changes.
- **FR-159**: Every context slice carries a trust level. Untrusted slices (channel content) follow FR-154 — data only: never stored, logged, or executed, never triggering commands, tools, database writes, or persona/permission/config changes — and the prompt builder embeds them only in data-marked regions. Trusted slices (config, persona, database facts) may be embedded normally. Secrets (NFR-3) are never prompt input.
- **FR-160**: Orchestration rules: collector calls are isolated (a failing collector is logged and its slice skipped — it never fails the run); the LLM gate (kill switch, daily budget, per-feature accounting — FR-102–FR-104) is checked before every model call; output is validated per surface before use; one structured log per run records trigger, feature, collectors used, outcome, and tokens — never content. Nothing pipeline-specific is persisted, and validated output goes only to the trigger's own sink (presence service, channel reply, memory tables) — never through `run_command` (FR-90).

### 2.26 LLM conversation participation — chat replies *(planned)*

Phase B of roadmap Phase #2 (memory & personality stay a separate future task). Builds directly on the §3.12 toolkit; the §2.25/§3.20 registry pipeline stays planned until memory needs it. The adaptive decision layer — model-gated SKIP/GO, attention score, schedule multiplier, reaction feedback, periodic LLM review, per-guild knob mirror — is roadmap **Phase B2** (reserve §2.27, next free FR-183), not part of this phase: here the decider is the heuristic roll only (FR-175–FR-177).

- **FR-170**: Per-guild opt-in for participation: `!llmreply [on|off]` (tier 2, guild-only, slash-eligible) shows/sets `GuildSetting.LlmReplyEnabled` (persisted, default false). A guild that has not opted in never triggers, reads chat for, or posts participation replies. Without `Llm:ApiKey` the command still works (like `!llmcontext`), but runs report inactive (FR-92).
- **FR-171**: Channel scope: participation reads context and posts only in the guild's allowlisted channels — the shared `GuildLlmChannel` table (FR-164; empty = all readable text channels incl. threads), the same allowlist that narrows chat-context reads. DMs are never read or answered.
- **FR-172**: Two trigger kinds create *candidates*; whether the bot replies is decided by FR-176/FR-177:
  - *keyword trigger* (message event): a human, non-bot, non-webhook message in an opted-in guild and allowlisted channel that mentions the bot — a real `@` mention (the bot user is in the message's `MentionedUsers`), or its name written out (a message word within ≤ 2 edits, and no more than ⌈name length / 3⌉ so short names stay strict, of the closest of the bot's own current names in that guild: nickname → global display name → username, read from the gateway cache per message, no configuration) — is a Discord reply referencing one of the bot's own messages, or contains a configured keyword (FR-174) as a case-insensitive whole-word match; command invocations (identifier prefix) never trigger. `@` and name mentions are one *mention* candidate kind (FR-176);
  - *periodic trigger*: a fixed-interval job on the shared `Scheduling/PeriodicJobHost` (§2.8/§3.8) every `Llm:Chat:CheckIntervalMinutes` (default 15, floor 1) evaluates each opted-in guild's allowlisted channels that had activity in the window (FR-175).
- **FR-173**: A candidate that passes the roll runs once, serialized per guild (semaphore): gate (kill switch, UTC-day budget, per-feature accounting under the feature name `chat` — FR-102–FR-104) → `LlmPromptBuilder` (persona via the guild-scoped chain, FR-156, plus a fixed participation instruction block) → context (FR-178) embedded as an untrusted data-marked region (FR-154/FR-159) → `ILlmClient` → `LlmOutputValidator` → plain channel message (FR-180). LLM output never goes through `run_command` (FR-90) and never triggers commands, tools, or database writes.
- **FR-174**: `!llmkeywords [add <keyword>|remove <keyword>|clear]` (tier 2, guild-only, slash-eligible) manages a per-guild `GuildLlmKeyword` table (key `(GuildId, Keyword)`); no args lists them. Keyword: non-empty, ≤ 100 chars, stored lowercase; matching is case-insensitive whole-word. `@` mentions, name mentions (resolved from the bot's own guild user, so a rename or per-guild nickname is picked up with no configuration), and reply-to-bot need no configuration. Changes apply from the next message, no restart.
- **FR-175**: Activity model: in-memory per-(guild, channel) counters of human messages (fed by the same `MessageReceived` subscription that evaluates the message trigger), kept over a rolling window `Llm:Chat:ActivityWindowMinutes` (default 60): < 5 → quiet (×0.25), 5–29 → normal (×1), ≥ 30 → hot (×2.5); if ≥ `Llm:Chat:BurstMessages` (default 5) messages arrived in the last `Llm:Chat:BurstWindowMinutes` (default 2), the channel counts as hot regardless. In-memory only — a restart resets the counters; activity re-learns within one window.
- **FR-176**: Probability model: `p = base(kind) × activity × boosts`, capped at `Llm:Chat:MaxProbability` (default 0.9). Base: mention (`@` or by name)/reply-to-bot `Llm:Chat:MentionProbability` (0.6), keyword `Llm:Chat:KeywordProbability` (0.4), periodic `Llm:Chat:PeriodicProbability` (0.08). Boosts: triggering message ends with `?` → ×`Llm:Chat:QuestionBoost` (1.5); the bot replied to the same author within `Llm:Chat:AuthorRotationMinutes` (60) → ×`Llm:Chat:AuthorRotationFactor` (0.5). One uniform random roll per candidate. All values are global config; absent/invalid → defaults.
- **FR-177**: Hard bounds (per guild, all checked before the roll, against in-memory per-UTC-day counters like FR-102): ≤ 1 unsolicited reply per `Llm:Chat:MinMessagesBetweenReplies` (default 10) messages and per `Llm:Chat:MinMinutesBetweenReplies` (default 30) minutes; ≥ `Llm:Chat:ChannelCooldownMinutes` (default 10) since the bot's last participation reply in that channel; the per-guild daily cap (FR-179); the global kill switch and daily budget (FR-103/FR-104).
- **FR-178**: Context: the triggering message (author username, content, UTC timestamp) plus the newest `Llm:Chat:ContextMessages` (default 5) messages of the same channel, read via the Discord REST API and formatted newest-first through the shared `ChatContextFormatter`; bot-authored lines in the window are marked as the bot's own. A REST failure degrades to the triggering message only — it never fails the run. Messages older than `Llm:ChatContext:MaxAgeHours` (default 24) are dropped (shared with FR-154).
- **FR-179**: Per-guild daily reply cap: `!llmreplycap <n|reset>` (tier 2, guild-only, slash-eligible) sets `GuildSetting.LlmReplyDailyCap` — whole number ≥ 0, `0` = unlimited; the default is `Llm:Chat:DefaultDailyReplyCap` (20); `reset` restores the configured default. Replies count per guild per UTC day in memory; at the cap no further candidates roll that day. The global `Llm:DailyRequestBudget` (FR-103) remains the cross-feature hard stop.
- **FR-180**: Output surface: a plain message in the triggering channel (no reply reference), validated non-empty, ≤ 2000 chars, single message, line breaks allowed — a new `LlmOutputValidator` surface (§3.12). Sent with `AllowedMentions.None`: the bot never mentions anyone, including @everyone/@role. The FR-154 output rules extend to replies: no quoting chat, no user or channel names. Invalid output → no message (FR-181).
- **FR-181**: Failure semantics (FR-94 shape): gate refusal, client error, or invalid output → nothing posted, logged without message content, counted toward the watchdog consecutive-failure alert (§3.13) and the `chat` feature accounting where a call was made, never retried for that candidate; nothing crashes the bot.
- **FR-182**: Renames the chat-context opt-in command `!llmchat` → `!llmcontext` (amends FR-154/FR-164/FR-165/FR-168 and §5): identical behavior and persistence (`GuildSetting.LlmIncludeChat`); the distinct name keeps the "what the LLM reads" opt-in separate from the participation commands `!llmreply`/`!llmreplycap`/`!llmkeywords`.

### 2.28 Embed-fix link reposts

X/Twitter and Reddit links unfurl badly in Discord (X serves no usable metadata; Reddit's own unfurl loses video audio and extra images). The mirror services below serve a post's metadata for the origin's own path, so a rewrite is a host swap. No LLM is involved and no link or message content is stored; the only network call is the bounded Reddit preview check (FR-200), which decides whether a Reddit repost is worth posting at all. One guild-level knob (on/off) plus one bot-owner mapping table; design §3.23.

| Origin hosts (subdomain-tolerant) | Shipped mirror host | Mirror project |
|---|---|---|
| `x.com`, `twitter.com` | `fxtwitter.com` (`fixupx.com` is the same service) | FixTweet / FxEmbed |
| `reddit.com` | `redditez.com` | redditez |

Equivalent mirrors an operator may map instead of a shipped default: `fixupx.com` (runs FixTweet's code), `vxtwitter.com` / `fixvx.com`, `rxddit.com`, `vxreddit.com` — swapping one is `!linkfixmap` (FR-198), never a code change.

- **FR-192**: Per-guild opt-in: `!linkfix [on|off]` (tier 2, guild-only, slash-eligible) shows/sets `GuildSetting.LinkFixEnabled` (persisted, default false). No global on-switch exists: a guild that has not opted in is never scanned, and DMs are never scanned or answered. The toggle is the only guild-level setting — the mapping is global (FR-198).
- **FR-193**: Detection: in an opted-in guild every message is scanned for absolute `http`/`https` URLs whose host matches an origin of the effective mapping (FR-195), case-insensitively, with `www`/`mobile`/`m`/`old`/`new`/`np` counted as the origin they prefix. A message never qualifies when authored by a bot or webhook (including the bot itself), when it is a command invocation (FR-7), when it contains no matching URL, or when its only URLs already point at a mirror host. No gateway intent changes (FR-2): `GuildMessages | MessageContent` suffice.
- **FR-194**: Rewrite: host swap only — scheme forced to `https`, host replaced by the origin's mirror host, path/query/fragment preserved verbatim, trailing punctuation (`.`, `,`, `)`, `]`, `>`) stripped from the URL. Only canonical post paths qualify (`/<user>/status/<id>` for X/Twitter, `/r/<sub>/comments/<id>/…` and the Reddit app's share form `/r/<sub>/s/<id>` for Reddit — the mirror resolves the share token itself, so that rewrite is still a host swap); short or opaque shapes (`redd.it/<id>`, `x.com/i/…`, profile and settings paths) are skipped rather than resolved, since resolving them would mean fetching.
- **FR-195**: Effective mapping: shipped defaults (table above) with runtime overrides applied (FR-198), resolved per message so a change applies from the next message with no restart. Exactly one mirror host per origin; no automatic mirror failover (that would require fetching the mirror).
- **FR-196**: Repost: exactly one follow-up per qualifying message — a reply referencing the original message whose content is the distinct rewritten URLs in first-appearance order, one per line and nothing else, sent with `AllowedMentions.None`. Duplicates collapse to one line. The original message's content is never edited or deleted (a bot cannot edit a member's message); no other message is touched. The one exception is Discord's per-message `SUPPRESS_EMBEDS` flag: after a repost posted and only when it carried **every** link in the message, the bot sets that flag so the faulty preview stops sitting beside its replacement (`LinkFix:SuppressOriginalEmbeds`, default true). Suppression is per message and all-or-nothing — a message with a link the bot left alone keeps its own previews — and it needs Manage Messages: without it the repost stands, the original keeps its embed, and the attempt is a debug log line (a deleted message or any other refusal logs a warning by exception type). Text content is never touched, and a suppression failure never affects the repost or the cooldown window.
- **FR-197**: Bounds: at most `LinkFix:MaxLinksPerMessage` (default 5) distinct links per repost — the surplus is dropped and the first N still post — and at most one repost per channel per `LinkFix:ChannelCooldownSeconds` (default 0 = disabled); only a repost that posted consumes the window, so a failed send releases it. Both bounds are in-memory only, so a restart resets the cooldown. Bot bans never suppress a repost (FR-135 covers replies to the invoker; a repost serves the channel), which makes these bounds the only anti-spam limit. Maintenance mode pauses scanning, and nothing is queued for later.
- **FR-198**: `!linkfixmap` — the bot-owner mapping table (tier 4, global across guilds, DM-usable, slash-eligible): no args shows the effective mapping with per-row layer labels (`runtime` / `shipped default`, the `!llmstatus` convention); `!linkfixmap <origin-host> <mirror-host>` sets or overrides a row and may add an origin the shipped table lacks, so a new site needs no code change; `!linkfixmap clear <origin-host>` drops the override (the shipped default returns; an override-only origin disappears). Arguments are bare lowercase hostnames (`^[a-z0-9.-]+$` — no scheme, path, port, or userinfo), an origin may not itself be a mirror host, and a mirror host may not be a known origin (so a rewrite never points back at the site it fixes). The table is capped at `LinkFix:MaxMappings` (default 50): past the cap new origins are rejected while existing rows still update. Overrides persist as `LinkFixMapping` rows (`OriginHost` key → `MirrorHost`, migrated by `AddLinkFix`) and survive a restart; that table is the only persisted mapping state, so origin→mirror hosts are deliberately not configuration.
- **FR-199**: Passivity, failure, privacy: the bot never resolves the target URL, calls no LLM, and consumes no gate or budget — the rewrite is textual, and its only network activity is FR-200's preview check (which never touches the target URL). Nothing about a link or message is persisted (only FR-192's toggle and FR-198's overrides) or logged as content; one structured log per repost records guild, channel, link count, and outcome. A failed send is logged without content, swallowed, never retried, and never crashes the bot, affects the original message, or raises a health/watchdog signal.
- **FR-200**: Reddit video gate: a Reddit link is reposted only when its mirror's preview declares a video (`og:video`) — the fix buys a video embed, while a text or image post unfurls fine on its own. The check requests the rewritten mirror URL once (following redirects, bounded by `LinkFix:VideoProbeTimeoutSeconds`, capped read), and only a conclusive answer is cached: metadata that declares no video, per URL, for `LinkFix:VideoProbeCacheMinutes` (capped at 500 entries, oldest evicted first, never persisted). A failed, timed-out, non-2xx, metadata-free, or oversized (capped read reached) response means no repost (fail closed) and is not cached, so the next message retries; the failure is logged by exception type only, so no URL or message text reaches the log. X/Twitter links and operator-added origins are never checked (X serves no metadata at all, and the bot knows nothing about an added site). `LinkFix:RedditVideoProbe` = false disables the check and reposts every eligible Reddit link.

Config (`LinkFix:*`; absent/invalid → default): `MaxLinksPerMessage` 5 · `ChannelCooldownSeconds` 0 (disabled) · `MaxMappings` 50 · `RedditVideoProbe` true · `VideoProbeTimeoutSeconds` 5 · `VideoProbeCacheMinutes` 360.

### 2.29 FuelUsageMonitor — Torque upload ingestion, receive path *(planned)*

Members drive with Torque Pro/Lite (Android + Bluetooth OBD-II adapter) and its **realtime web upload**: the app sends one sample per logging interval as query-string parameters (`GET` or `POST`, empty body) and treats HTTP 200 with body exactly `OK!` as its only success signal. The wire format is captured in [`torque-log-upload-server-wire-format-and-endpoint-requirements.md`](torque-log-upload-server-wire-format-and-endpoint-requirements.md); design §3.24 covers the feature.

This section specifies **only the receive path** — authenticating, bounding, accepting and acknowledging samples, and accepting an uploaded body without using it (FR-212) — so telemetry can start flowing. Accumulating samples into trips, the fuel and distance statistics, persistence and the per-trip Discord summary are the second half and are specified separately before that work starts; nothing here posts to Discord or writes to the database.

- **FR-201**: Opt-in inbound endpoint: `FuelUsageMonitor:Enabled` (default false) plus the secret `FuelUsageMonitor:UploadToken` (NFR-3/NFR-4). Enabled without a token is a startup validation failure — critical log, non-zero exit — matching the `Mcp:ApiKey` rule (FR-86), and the startup log names the endpoint's state. It is served by the in-process Kestrel host that already runs the MCP endpoint (§3.11) at `FuelUsageMonitor:ListenUri` (default `http://0.0.0.0:2526`): no second process and no second host. When disabled, no route is mapped at all and per-request logging stays silent.
- **FR-202**: Endpoint contract: `GET` and `POST` on `FuelUsageMonitor:ListenPath` (default `/fuel/upload`) are equivalent and both accepted, because the app's method is not dependable. **Every parameter is read from the query string; the request body carries no data the bot uses** — Torque sends an empty body, so a body-parsing endpoint silently receives nothing. A body may nevertheless arrive and is accepted without being used (FR-212); no other path is served (a wrong path is `404`, worded so it never confirms which paths exist).
- **FR-203**: Success signal: every request that passes FR-204/FR-205 and is not maintenance-refused (FR-209) is answered `200` with a `text/plain` body of exactly `OK!` — the string the app checks while driving and from its *Test settings* button. That test button fires a single request with no PID data; it is acknowledged like any other and is simply not a sample (FR-206). The acknowledgement is written before any sample work begins (FR-207), so the app is never held open by the bot's processing.
- **FR-204**: Authentication — two mechanisms, each enforced when configured, because Torque sends its bearer header only to an `https://` URL while a LAN/VPN deployment may run plain HTTP: the `Authorization: Bearer <Fuel:UploadToken>` header (constant-time compare; Torque Pro ≥ 1.12.46 sends it) must match, and when `FuelUsageMonitor:PathSecret` is set the request path must also end with that segment. A wrong or missing bearer is `401`, a wrong path secret is `404`; a request that fails either is accepted nowhere, stored nowhere, and is answered without revealing which check failed. Secrets default to the header rather than the URL, because reverse proxies and access logs record query strings.
- **FR-205**: Bounds: at most `FuelUsageMonitor:MaxUploadsPerMinute` (default 120) requests per remote address — in memory, per address — and at most `FuelUsageMonitor:MaxQueryBytes` (default 8192) of query string (Torque's own URLs are a few hundred bytes). Excess is answered `429`, an oversized query `414`; both are counted, never parsed, never queued, and are refusals of malformed external input rather than bot failures (FR-209).
- **FR-206**: Sample parsing: a request is a **sample** when it carries `time` (epoch ms) plus `session` or `id`. The parsed sample keeps `v` (format version, stored as received and never assumed), `session`, `id`, `time`, `eml` (the app's *User Email Address* field verbatim) and `profileName` when present, plus every `k*` PID key as an invariant-culture float. The `k*` set is open-ended and differs request to request: unknown keys are carried, never rejected and never required, and `0.0` is a real reading. A key repeated within one query resolves last-occurrence-wins (one documented policy). An unparsable numeric value makes that single key absent, not the sample invalid; a missing `session`/`id` or unusable `time` means the request is acknowledged (FR-203) but is not a sample. `userUnit*`, `defaultUnit*` and `profile*` keys are ignored. Every value is untrusted input — data only, never a command, a prompt, or interpolated text (the FR-159 trust rule; parameterised SQL when the second half persists anything).
- **FR-207**: Asynchronous handoff: the endpoint's only work is authentication, bounds and the syntactic parse of FR-206. The parsed sample is enqueued on a bounded in-process channel (`FuelUsageMonitor:QueueCapacity`, default 1000) consumed by a background service, with the `OK!` response written first. A full queue **drops** the sample rather than blocking or slowing the response — the drop is counted and logged once per interval without content — because the app retries and alerts on a slow or failed upload, so back-pressuring it loses more data than dropping does.
- **FR-208**: Reception visibility (this phase's only output): in-memory counters — samples accepted, rejected per reason, dropped, discarded request bodies (count and total bytes), last-seen UTC timestamp, last `session`, and the distinct PID keys seen — surfaced as a `fuelusage` row in `!health` (§3.13) and as one structured event per accepted sample carrying counts and key names only. Senders stay keyed in memory and are never named in logs or diagnostics: no coordinate, PID value or `eml` ever reaches a log line. Nothing is written to the database in this phase — samples are received, counted and discarded — and a restart resets the counters.
- **FR-209**: Failure semantics: a request refused by authentication, bounds, parsing or maintenance is answered with its specified status and logged as one structured event (outcome, byte count, no content). Inbound garbage is external input, not bot degradation: it never counts toward the watchdog consecutive-failure alert (§3.13), never marks a health failure, and never crashes the endpoint, the background consumer, or the gateway. While maintenance mode is active (FR-98) the endpoint answers `503` and accepts nothing, queuing nothing for replay.
- **FR-210**: Configuration (`FuelUsageMonitor:*`; absent/invalid → default): `Enabled` false · `ListenUri` `http://0.0.0.0:2526` · `ListenPath` `/fuel/upload` · `UploadToken` (secret, required when enabled; `FUELUSAGEMONITOR_UPLOAD_TOKEN` in compose, NFR-4) · `PathSecret` (optional, for plain-HTTP/VPN deployments) · `MaxUploadsPerMinute` 120 · `MaxQueryBytes` 8192 · `MaxBodyBytes` 8388608 (8 MB) · `QueueCapacity` 1000. The publish itself is loopback-only or proxy-terminated (§5), and the keys are documented in design §4.
- **FR-211**: Scope boundaries: the receiver adds no gateway intents (FR-2), no commands, no LLM call, no gate or budget consumption, and touches no guild state — trip statistics, the `FuelUsageTrip` table, the commands and the channel post arrive with the second half. No uploaded body is parsed, decoded, stored or kept in any form (FR-212), and it never resolves, follows or echoes an uploaded URL, and it is machine-to-machine ingestion of a member's own telemetry, not the user-facing web interface §6 rules out.
- **FR-212**: Uploaded bodies are accepted and discarded: a request may carry a body — Torque's finished-log share, a hand-rolled test upload — of up to `FuelUsageMonitor:MaxBodyBytes` (default 8 MB), which is read only far enough to finish the exchange: never parsed, never written to a temporary file or the database, never logged, with its content type and filename ignored so a `multipart/form-data` body is not interpreted as form data. A body never changes what the request means: without a sample in the query string (FR-206) it is merely acknowledged (FR-203) as not-a-sample, and a request that carries both a sample and a body is handled exactly as if the body were absent. Past the cap the request is refused `413`, nothing beyond the cap is read, and the refusal is counted like any other (FR-205/FR-209). Reading a body is bounded and streaming — no `IFormFile` binding, no multipart parsing, no buffering of the whole body — so an upload can neither exhaust memory nor leave a file behind.

## 3. Non-Functional Requirements

- **NFR-1 Platform**: .NET 10 (`net10.0`), long-lived generic-host worker.
- **NFR-2 Configurability**: Bot name, log format, and command identifier configurable without code changes.
- **NFR-3 Secret handling**: Token and connection string never committed (`appsecrets.json` gitignored, loaded at startup for source runs).
- **NFR-4 Secret-free images**: Containers receive secrets via environment variables; `appsecrets.json` excluded from publish output.
- **NFR-5 Testability**: Core command infrastructure, naming, permissions, error paths, and startup validation covered by unit tests without a live database or Discord connection.
- **NFR-6 Build & CI**: `dotnet build/test HambotV2.sln` on the .NET 10 SDK; the Jenkins pipeline builds/tests every push and auto-deploys successful version-tag builds only (verified against startup lines, rollback on failure).
- **NFR-7 Warning-free build**: No nullable warnings; CI enforces warnings-as-errors.
- **NFR-8 Deployability**: Deployable as a Docker container from `HambotV2/Dockerfile` (repo-root context); with valid secrets it reaches gateway Ready.
- **NFR-9 Extensibility**: New commands need only an `ICommand` implementation and a registration entry.
- **NFR-10 Resource isolation**: Per-message transient services, disposed after each message.
- **NFR-11 Audit logging**: Command executions traceable from logs without debugging.
- **NFR-12 Rate limiting**: Per-user/command throttling with a user-friendly reply; windows configurable per tier globally and per guild at runtime.
- **NFR-13 Argument parsing**: Typed, validated arguments through a shared mechanism.
- **NFR-14 Quotes testability**: Quotes covered without a live database or Discord (lifecycle, numbering, isolation, boundaries, search cap, gating, DM declines).
- **NFR-15 Custom-response testability**: Covered without live dependencies (validation, collisions, dispatch order and gating, attachment rejection, DM behavior).
- **NFR-16 Reminders testability**: Covered without live dependencies (`FakeTimeProvider`, InMemory): parsing, delivery scheduling, restart recovery, bounded failure.
- **NFR-17 Version testability**: Version formatting (pure string logic) and `!version` covered by unit tests.
- **NFR-18 Presence testability**: Presence/bio/avatar/banner covered against mocked gateway and fake downloader: config validation, commands, serialization, failure paths, not-persisted semantics.
- **NFR-19 Backup drill**: The restore procedure is scripted, documented, and proven once against a scratch database; validation and restore-test outcomes feed `!health` and the watchdog.
- **NFR-20 MCP testability**: Covered without live transport/database/Discord: auth, every tool over fakes, `run_command` capture/authority/cooldown/audit.
- **NFR-21 LLM testability**: Covered with a fake client, no network: prompts (presence and bio), both validation boundaries (128/190 chars), FR-154 input assembly (chat-context bounds, DM exclusion, untrusted content never stored or logged), scheduling, failure path, inactive-without-key; persona resolution chain (guild → global → config → seed) and the override commands (FR-155/FR-156); model resolution chain, `!model list`, and set-validation against the API model list (FR-161/FR-162); the bio interval parsing and the manual `!llmupdate` trigger outcomes (FR-163); the per-guild channel allowlist (FR-164–FR-166): per-guild filtering, empty-means-all, the opt-in still gating, and the `!llmchannels` add/remove/clear command behavior; the runtime interval override (FR-167): set/reset/floor/usage errors and the effective interval shown in `!llmstatus`.
- **NFR-22 Backup command testability**: Covered against a fake backup manager: trigger, list, two-step confirmation, maintenance gating, tier gate, failure replies.
- **NFR-23 Watchdog testability**: Covered with fakes: each condition's detection, alert cadence, delivery paths, `!health` fields, notification rate limit/collapse, `!notifychannel` persistence.
- **NFR-24 Recurring-reminder testability**: Covered like NFR-16: `every` parsing, redelivery without bursts, numbering stability, the separate cap, one-shot independence.
- **NFR-25 LLM guardrail testability**: Covered with a fake client: accounting, budget suspension and rollover, kill-switch states and replies.
- **NFR-26 Application-command testability**: Covered without live Discord: interaction→command-line mapping, shared pipeline semantics, ephemerality, the registration contract.
- **NFR-27 Scan automation**: The scan is defined as code and runs end-to-end non-interactively on a weekly schedule; agent tooling is installable non-interactively.
- **NFR-28 Scan verification**: Pipeline-only (no `BotTests`); verified by a live run producing and archiving the SBOMs/reports without touching the stack.
- **NFR-29 Quote-of-the-day testability**: Covered without live dependencies: template validation/rendering, schedule parsing, boundary math, publication behaviors (baseline, skip, failure, maintenance), and the command's behavior.
- **NFR-30 Bot-ban testability**: Covered without live dependencies: pipeline enforcement (silent drop on both surfaces), scoping, exemptions, audit outcome, lazy expiry, and the three commands' behavior.
- **NFR-31 Suggestion testability**: The matcher is pure logic, unit-tested at its boundaries; the pipeline path is covered without live dependencies: typo → suggestion, garbage → plain reply, tier exclusion (no leakage), DM scope, custom-response candidates, and a virtual `run_command` invocation capturing the suggestion.
- **NFR-32 Shared-matcher testability**: The matcher is unit-tested once at its boundaries; each consumer is covered at its own path — suggestions per NFR-31, quote-search matching and ranking (substring precedence, distance ties), and member-search ranking over the fake `IMcpDiscordClient`.

- **NFR-33 Context-pipeline testability/extensibility**: Adding a trigger or collector needs only its interface plus one DI registration (no orchestrator changes); covered with a fake LLM client and fake triggers/collectors — registry resolution, enable/opt-in filtering, trust placement in the assembled prompt, slice-failure isolation, gate/budget interaction, per-run logging without content.
- **NFR-34 LLM-participation testability** *(planned)*: Covered with a fake `ILlmClient`, `FakeTimeProvider`, InMemory, and an injectable random source — no network/Discord: trigger evaluation (mention by `@`, name mention with typo tolerance against the bot's username/display name/nickname, reply-to-bot, keyword whole-word case-insensitive match, command-prefix/bot/webhook exclusion, non-opted-in guild, non-allowlisted channel, DM); activity tiers incl. burst override and window rollover; probability-model boundaries (each base, multiplier, boost, the `MaxProbability` cap, roll determinism); hard bounds (message/minute cadence, channel cooldown, daily cap incl. `0` = unlimited and UTC rollover); context assembly (triggering message + channel window, REST-failure degradation, bot lines marked, untrusted data-marked placement, max-age drop); output validation (≤ 2000, `AllowedMentions.None`, no-quote/no-names rule); gate interaction (kill switch, budget, `chat` accounting, watchdog failure counting); per-guild run serialization; and the `!llmreply`/`!llmreplycap`/`!llmkeywords` commands.
- **NFR-35 Link-repost testability**: Covered without live Discord, database, or network (mocked message surface and preview seam, InMemory): detection (each origin host and its subdomain variants, case-insensitivity, non-matching hosts, bot/webhook/own-message/command-invocation exclusion, non-opted-in guild, DM); rewrite (path/query/fragment preservation, scheme forcing, trailing punctuation, post-path eligibility, short-link skipping); mapping layering (shipped default, override wins, `clear` restores or removes, a change applying from the next message, persistence across restart); the Reddit video gate (video kept, no-video dropped, failure and timeout fail closed without logging the URL, X never checked, mixed messages partially kept, verdict cached and re-checked after expiry, a no-metadata answer not cached, the cache bounded and evicting the oldest, gate disabled, and a filtered message not consuming the cooldown window); the probe's own HTTP path over a fake transport (request target and Discordbot user agent, non-2xx, capped read, timeout) and the pure `og:video`/`og:` classification; bounds (per-message cap, channel cooldown incl. `0` disabled and restart reset); repost shape (one reply, dedupe, ordering, `AllowedMentions.None`); both commands incl. hostname validation, the origin-is-not-a-mirror rule, and the mapping cap; and that no link or message content is stored or logged.
- **NFR-36 Torque-ingestion testability** *(planned)*: Covered without live Discord, database or network — the endpoint driven in-process over its own HTTP factory seam: method parity (`GET` and `POST` both accepted), query-only parsing (a supplied body never contributes a parameter), the *Test settings* empty-query request, the exact `OK!` body and the guarantee it precedes consumer work, bearer and path-secret authentication (missing, wrong, and no information leaked about which failed), the two query bounds (`429`, `414`), body handling (a multipart upload acknowledged and discarded with nothing written to disk or the database, a CSV body the same, a body alongside a real sample changing nothing, over-cap `413`, the discarded count and byte total), sample parsing (`v`/`session`/`id`/`time`/`eml`/`profileName`, last-occurrence-wins duplicate keys, unseen `k*` keys carried, unparsable values dropped per key, `0.0` readings, epoch-ms conversion, a non-sample request acknowledged but not queued, ignored unit/profile keys), queue saturation dropping instead of blocking, maintenance refusal, counters and the `!health` row, no-content/no-coordinate/no-`eml` logging, and that nothing is persisted.
- **NFR-37 Autofix verification**: Pipeline-only (no `BotTests`); verified by a live run — a scan with auto-fixable CRITICAL findings produces a branch + PR with a green build/test run, and a scan without them produces no PR.

## 4. Constraints

- **C-1**: A valid Discord bot token is required to run.
- **C-2**: A reachable PostgreSQL instance is required (startup and persistence; containers assume `host.docker.internal:5432` unless composed).
- **C-3**: The Message Content intent must be enabled in the developer portal.
- **C-4**: Command names must be lowercase.
- **C-5**: Releases are `v<major.minor.patch>` git tags; a build's version derives from the latest such tag plus the commit hash (FR-76).

## 5. Command Interface

The full command list with arguments and default tiers is in [`software-design.md`](software-design.md) §3.5 and the command-family sections, and in the README's command table. Cross-cutting rules:

- Argument errors produce in-channel errors. Guild-state commands decline in DMs ("only available in servers"); the reminder commands and the bot-owner global commands are DM-usable.
- An unknown token is checked against the guild's custom responses (same permission and cooldown checks) before the "unavailable" reply; custom responses never apply in DMs. When no match exists there either, a sufficiently close runnable candidate appends one "did you mean" suggestion line to the reply (FR-144–FR-148, text surface only — the slash surface has Discord's own autocomplete).
- When `SlashCommands:Enabled` is set, the administrative/configuration commands (including the LLM commands `llmstatus`, `llmswitch`, `llmupdate`, `llminterval`, `model`, `persona`, `llmcontext`, `llmchannels` — plus, planned per §2.26, `llmreply`, `llmreplycap`, `llmkeywords` — and `linkfix`, `linkfixmap`), the reminder commands, `!searchquotes`, `!version`, and `!help` are additionally exposed as ephemeral slash commands with identical tiers and cooldowns (FR-110–FR-112, FR-119); the remaining community-facing commands stay text-only.
- Link reposting (§2.28) is passive, not a command: `!linkfix` is guild-only, `!linkfixmap` is bot-owner global and DM-usable, and members who post links are never limited by the command cooldown — the feature's own bounds (FR-197) are the only limit.

## 6. Out of Scope

- Per-channel configuration (per-guild is in scope, FR-24–FR-26).
- Usage analytics.
- File/image attachments in custom responses (text only, FR-60).
- A slash surface for the remaining community-facing commands, or custom responses as application commands (FR-110/FR-119).
- "Did you mean" suggestions on the slash surface (FR-144) — Discord's autocomplete already guides users to the registered commands.
- A web interface or admin panel (permissions are chat-managed; the MCP endpoint is an agent API, not an admin UI).
- A user-facing web surface for trip data: the Torque upload endpoint (§2.29) is machine-to-machine ingestion of a member's own telemetry, not a UI.
- Markdown-table help rendering (Discord does not render tables; bold group headers, FR-133).
- Streaming-type presence activities (FR-78).
- LLM conversation participation, memories, and personality beyond the persona seed (planned, [`roadmap.md`](roadmap.md)).
- LLM-generated avatars/banners (images are set manually via `!avatar`/`!banner`).
- Fully automatic dependency remediation: CRITICAL findings get an automated fix PR (FR-214), but a human still reviews and merges it — merging is never automatic.
- Bot-side notifications from the scan pipeline (it deliberately holds no bot credentials, FR-126).
- Per-guild origin→mirror mappings, and per-guild other link-repost settings beyond the on/off toggle — the mapping is bot-owner global (FR-198).
- Resolving short links (`redd.it/<id>`, `t.co/…` — skipped instead, FR-194), automatic mirror failover, and fetching or validating anything beyond FR-200's bounded Reddit preview check.
- Rewriting links in the bot's own output, in custom responses, or on the slash surface — reposting is driven by member messages in opted-in guilds only (FR-193).
