# Software Requirements

The requirements for HambotV2 — the software as it *should be*, traceable when the system changes. Sections 2.11–2.23 are the post-1.0.0 features; the unimplemented ones are the outstanding [`roadmap.md`](roadmap.md) backlog.

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
- **FR-46**: `!searchquotes <text...>` — case-insensitive substring match, number-prefixed, ≤ 10 results; no matches → friendly reply; empty term → in-channel error.
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
- **FR-143**: `search_users(guildId, query, limit ≤ 25)` — a case-insensitive member search over username, display name, and server nickname, resolving a name the agent encountered (a message author, "who is X") to a user id for the other lookup tools; backed by Discord's REST member search within the bot's existing guild access. A missing query is an error; members who left are not findable (current members only).
- **FR-142**: The lookup tools (FR-139–FR-143) are read-only: nothing is written, executed, or posted, so the operator-agent tier (FR-120) does not gate them; they share the endpoint's authentication (FR-87), tracing, and error semantics, and read only what the bot already holds or can already read (no new gateway intents, no external data enrichment). During maintenance mode (FR-98) they decline with the same captured notice as `run_command`. The bot compiles and stores nothing — interpreting the records is the calling agent's job.

### 2.14 LLM integration (phase 1: presence updates) *(planned)*

- **FR-92**: A Mistral LLM client (`Llm:ApiKey`, `Llm:Model` default `mistral-small-latest`); no key → all LLM features inactive.
- **FR-93**: An opt-in presence-update job (`Llm:PresenceUpdates:Enabled`, interval default 60 min): prompt from the persona seed + bot name/version + date/time + current presence; validate output (non-empty, single line, ≤ 128 chars); apply via the presence service.
- **FR-94**: LLM/API failures leave the current state unchanged, are logged, retry next interval, and never crash the bot.
- **FR-95**: The LLM client is an interface with a Mistral HTTP implementation, so prompts/validation/scheduling are unit-testable without network.
- **FR-99**: A documented, configuration-overridable default persona seed ships with the bot; authoring and tuning it is part of the feature work.
- **FR-102**: Usage is accounted per feature per UTC day (requests, tokens in/out) and exposed to `!llm status` and `!health`; in-memory.
- **FR-103**: A daily request budget (`Llm:DailyRequestBudget`, default 500) hard-stops API calls for the rest of the UTC day; `0` disables the feature set like an absent key.
- **FR-104**: `!llm status|off|on` manages the LLM features at runtime (tier bot owner, DM-usable); the kill switch is in-memory — a restart restores the configured state.
- **FR-114**: With `Llm:BioUpdates:Enabled`, the same job refreshes the bio (≤ 190 chars) with identical validation, failure handling, and budget accounting.

### 2.15 Watchdog, health reporting & operator notifications

- **FR-105**: `!health` reports version, uptime, gateway state, database reachability, backup/restore-test outcomes, LLM state (when it lands), MCP state, pending reminder count, and the notification channel. Tier bot owner; DM-usable.
- **FR-106**: A watchdog loop (default 5 min) alerts the bot owner by DM on: gateway down past a threshold (default 10 min); backup validation failure, overdue backup, or failed/overdue restore test; LLM budget exhaustion or three consecutive LLM failures; maintenance mode. Each condition alerts once, re-alerts at most hourly, announces recovery once; also posted to the notification channel; log-only without a configured owner; checks never crash the bot.
- **FR-117**: An operator notification channel: configurable default (`Notifications:ChannelId`), runtime-managed via `!notifychannel` (show / `<channel mention or id>` / `clear`) persisting in global storage; tier bot owner; DM-usable.
- **FR-118**: With a channel set, watchdog alerts/recoveries, command failures, retry-exhausted reminders, maintenance/restore outcomes, and notification delivery failures are posted there, rate-limited (default 5/min) with identical consecutive messages collapsed; failed posts never affect the originating feature; startup criticals stay log-only.

### 2.16 Recurring reminders

- **FR-107**: `!remindme every <duration> <message...>` creates a recurring reminder: first due after the interval, re-scheduled from the actual delivery time each delivery (downtime shifts the schedule, no catch-up burst); interval floor configurable (default 10 min).
- **FR-108**: Recurring reminders keep their number across deliveries; `!reminders` marks them; `!cancelreminder` cancels permanently; retry exhaustion stops the recurrence and marks it failed.
- **FR-109**: A separate per-user recurring cap (default 5), never consuming one-shot slots.

### 2.17 Discord application commands (slash commands)

- **FR-110**: Opt-in (`SlashCommands:Enabled`, default false). Eligible: administrative/configuration commands, the user-scoped reminder commands, and `!help` (as `/help`); the quote, custom-response, and `version` commands stay text-only. Eligibility is declared per command; definitions are built from the same registry and bulk-overwritten idempotently on startup; custom responses are not registered.
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

### 2.19 Quote of the day (scheduled publication)

- **FR-127**: Per-guild configuration on the guild's settings: target channel (null = off — the only activation switch), template (default `quote of the day: {quote}`), interval (default 24 h), and anchor (default 00:00 UTC). Entirely UTC, no per-guild time zones; configurable deployment-level interval floor (default 1 min).
- **FR-128**: `!quoteoftheday` (admin tier, guild-only, slash-eligible): no args report the config or "off"; `channel <#ch|id>` / `channel clear` activate/deactivate; `template <text...>` (non-empty, ≤ 500 chars, contains `{quote}`); `<due time> <interval>` sets the schedule (due time is a single duration, its end becomes the anchor; interval must be whole minutes ≥ the floor). Schedule changes take effect at the next boundary of the new schedule, never mid-boundary; channel/template changes leave the schedule untouched.
- **FR-129**: On a boundary the bot publishes one random quote of the guild through the template (capped at Discord's limit with an ellipsis); a guild with no quotes skips; maintenance mode pauses publications with no catch-up burst.
- **FR-130**: Fixed wall-clock boundaries (anchor + whole multiples of the interval, UTC) — same time every day, no drift from late/failed posts; boundaries passed while down or off are skipped, at most one publication per boundary; the next due time is derived from configuration.
- **FR-131**: A failed post is logged, skips its boundary, and leaves the schedule and the feature unchanged; no upfront channel validation; no new gateway intents.

### 2.20 Help command grouping (readability)

- **FR-132**: Every command declares one category from a fixed code-defined set (General, Quotes, Custom responses, Reminders, Permissions & settings, Moderation, Backups & stability, Bot profile); undefined categories are rejected at registration. Presentation-only.
- **FR-133**: Grouping never leaks: only commands the invoker can see are rendered, empty groups are omitted, and both help surfaces render identically. Pagination stays group-aware: a group that fits a page is never split; an oversized group repeats its header on continuation pages.

### 2.21 User bot bans (moderation)

- **FR-134**: Per-guild `BotBan` entity (guild + user id, banning user, creation, nullable expiry — null = permanent); guild-scoped (DMs never blocked); re-banning updates the record.
- **FR-135**: Enforcement at the very front of the pipeline, on both surfaces: an active ban drops every invocation silently — no reply of any kind — audited with outcome `Banned`.
- **FR-136**: The guild owner and bot owner can never be banned and bypass the check; the invoker cannot ban themselves; operator-agent invocations bypass the check.
- **FR-137**: Temporary bans lift lazily at read time — an expired record is deleted on sight; no scheduled job.
- **FR-138**: Server-scoped moderator-tier commands (declining in DMs, Moderation category, slash-eligible): `!banuser <user> [duration]` (reminder duration forms; escalation guard applies; invalid input stores nothing), `!unbanuser <user>` (deliberately unguarded), `!banlist` (soonest expiry first, ≤ 10 entries, expired lifted on sight).

### 2.22 Unknown-command suggestions ("did you mean?") *(planned)*

- **FR-144**: When an invocation's name resolves to neither a built-in command (FR-11) nor, in a guild, a custom response (FR-54), the pipeline computes at most one suggestion before sending the "unavailable" reply. Exact-name matches never reach the suggestion path — built-ins and custom responses win by construction (FR-53, FR-54). Applies to the text surface and MCP `run_command` only; the slash surface is excluded — Discord's own autocomplete already guides users there, and unknown names cannot be invoked on it at all.
- **FR-145**: Candidates are all registered built-in command names plus, in a guild, the invoking guild's custom response names — they dispatch from the same path, so a typo of one is as dead an end as a typo of a built-in. DMs consider built-ins only (FR-59). A custom response is suggested by its bare name, like a command.
- **FR-146**: Matching is case-insensitive. A candidate qualifies iff its Levenshtein edit distance from the attempted token is ≤ 2, or the candidate begins with the token and the token is ≥ 3 characters (so `rem` finds `remindme`). The single best qualifying candidate is suggested — lowest distance wins, ties broken alphabetically. Nothing qualifies → the existing reply, unchanged.
- **FR-147**: Suggestions never leak commands the invoker cannot run: candidates whose effective required tier exceeds the invoker's effective tier are excluded before matching (per-guild tiers in a guild, FR-17; built-in defaults in DMs, FR-25; the operator-agent grant for virtual invocations, FR-120) — mirroring FR-40's never-reveal rule. Banned users never get a suggestion (FR-135 drops them before this path).
- **FR-148**: The suggestion is one extra line appended to the "unavailable" reply, e.g. `Did you mean !quote?`, using the invocation's effective command identifier (FR-7). It is part of the channel reply itself, so virtual invocations capture it with no special handling; the audit event stays `UnknownCommand`, no content logged.

### 2.23 Fuzzy search — typo tolerance via the shared matcher *(planned)*

- **FR-149**: The fuzzy-matching logic (the FR-146 algorithm: case-insensitive Levenshtein distance with a per-consumer qualification rule) is one shared pure matcher. The suggestion path (FR-144–FR-148), `!searchquotes` (FR-150), and `search_users` (FR-151) all use it — no per-feature fuzzy implementations.
- **FR-150**: `!searchquotes <text...>` matches a quote iff it contains the term case-insensitively (unchanged, FR-46) or one of its whitespace-delimited words is within edit distance ≤ 2 of the whole term. Substring matches rank ahead of fuzzy matches; fuzzy matches rank by distance, ties by quote number ascending. The ≤ 10 cap and the no-match/empty-term replies are unchanged.
- **FR-151**: `search_users(guildId, query, limit ≤ 25)` keeps its Discord REST candidate set (current members only, FR-143) but ranks it locally with the matcher: distance of the query to the closest of username, display name, and nickname — lowest first, ties broken alphabetically — so the closest name leads. The cap and error semantics are unchanged.

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
- **NFR-21 LLM testability**: Covered with a fake client, no network: prompts, validation boundaries, scheduling, failure path, inactive-without-key.
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

## 4. Constraints

- **C-1**: A valid Discord bot token is required to run.
- **C-2**: A reachable PostgreSQL instance is required (startup and persistence; containers assume `host.docker.internal:5432` unless composed).
- **C-3**: The Message Content intent must be enabled in the developer portal.
- **C-4**: Command names must be lowercase.
- **C-5**: Releases are `v<major.minor.patch>` git tags; a build's version derives from the latest such tag plus the commit hash (FR-76).

## 5. Command Interface

The full command list with arguments and default tiers is in [`software-design.md`](software-design.md) §3.5 and the command-family sections, and in the README's command table. Cross-cutting rules:

- Argument errors produce in-channel errors. Guild-state commands decline in DMs ("only available in servers"); the reminder commands and the bot-owner global commands are DM-usable.
- An unknown token is checked against the guild's custom responses (same permission and cooldown checks) before the "unavailable" reply; custom responses never apply in DMs. *(Planned)* When no match exists there either, a sufficiently close runnable candidate appends one "did you mean" suggestion line to the reply (FR-144–FR-148, text surface only — the slash surface has Discord's own autocomplete).
- *(Planned)* The administrative/configuration commands, the reminder commands, and `!help` will additionally be exposed as ephemeral slash commands with identical tiers and cooldowns (FR-110–FR-112, FR-119); community-facing commands stay text-only.

## 6. Out of Scope

- Per-channel configuration (per-guild is in scope, FR-24–FR-26).
- Usage analytics.
- File/image attachments in custom responses (text only, FR-60).
- A slash surface for the community-facing commands, or custom responses as application commands (FR-110/FR-119).
- "Did you mean" suggestions on the slash surface (FR-144) — Discord's autocomplete already guides users to the registered commands.
- A web interface or admin panel (permissions are chat-managed; the MCP endpoint is an agent API, not an admin UI).
- Markdown-table help rendering (Discord does not render tables; bold group headers, FR-133).
- Streaming-type presence activities (FR-78).
- LLM conversation participation, memories, and personality beyond the persona seed (planned later phases, [`roadmap.md`](roadmap.md)).
- LLM-generated avatars/banners (images are set manually via `!avatar`/`!banner`).
- Automatic dependency remediation (the scan reports; updating packages is a manual operator decision).
- Bot-side notifications from the scan pipeline (it deliberately holds no bot credentials, FR-126).
