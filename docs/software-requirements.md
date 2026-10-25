# Software Requirements

This document captures the requirements for HambotV2, derived from the implemented system, its tests, and repository conventions. It describes the software as it *should be*, so requirements stay traceable when the system changes.

## 1. Product Description

HambotV2 ("HamBot") is a Discord chat bot. It listens to messages in the guilds the bot account is a member of, recognizes messages that start with a configurable command identifier (default `!`), and executes registered commands. Command access is governed by a tiered permission system persisted in PostgreSQL and scoped per Discord guild.

## 2. Functional Requirements

### 2.1 Discord connectivity

- **FR-1**: The bot shall connect to the Discord gateway using a bot token supplied via configuration (`Secrets:BotToken`), kept outside source control (see NFR-3).
- **FR-2**: The bot shall request only the gateway intents it uses (`Guilds | GuildMessages | DirectMessages | MessageContent`), rather than `GatewayIntents.AllUnprivileged`, so Discord.Net does not warn about unused intents.
- **FR-3**: The bot shall log Discord gateway events to the host logger using a configurable log format (`BotConfiguration:LogFormat`).
- **FR-4**: The bot shall log its gateway connection state periodically (every 5 minutes) while running.
- **FR-5**: The bot shall run continuously as a background worker service until cancelled.
- **FR-6**: On host shutdown the bot shall log out and dispose the Discord client cleanly; gateway disconnects and reconnects shall be logged.

### 2.2 Command recognition

- **FR-7**: The bot shall treat any received message starting with the effective command identifier of its guild as a command invocation (per-guild override, global default `BotConfiguration:CommandIdentifier`, default `!`; see FR-26). Direct messages use the global default.
- **FR-8**: The command name shall be the first space-delimited token of the message, with the identifier stripped and the remainder lowercased before lookup.
- **FR-9**: Degenerate command invocations — identifier-only messages (e.g. a bare `!`), whitespace-only content, or empty tokens — shall be handled safely, without exceptions.
- **FR-10**: If the command name is not registered, the bot shall consult the invoking guild's custom responses (FR-54) before replying in-channel that the requested command is unavailable.
- **FR-11**: Registered commands shall be looked up by name; command names must be lowercase (enforced by unit tests).

### 2.3 Command execution & error handling

- **FR-12**: Each command shall be a class implementing `ICommand` (`Name` + `ExecuteCommand(SocketMessage, ICommandContext)`), registered at startup through the command registration chain. Commands shall also provide their help text (FR-39) as part of the implementation.
- **FR-13**: A command shall receive an execution context wrapping a database context scoped to the message being handled.
- **FR-14**: If the invoking user lacks sufficient permissions, the bot shall reply in-channel that the user is not permitted to use the command, and the command shall not execute.
- **FR-15**: A failure in a command, permission check, or database access shall produce a user-facing error message in-channel and a logged exception; it shall never stop the bot from processing subsequent messages. Exception details shall not be exposed in-channel.
- **FR-16**: Per-message resources (command handler, database context) shall always be released after handling a message, including on the failure path.

### 2.4 Permissions (per guild)

- **FR-17**: Access control shall be tier-based: a user may execute a command if and only if the user's permission tier is greater than or equal to the command's required permission tier, both evaluated within the guild the message was sent in.
- **FR-18**: Users without a database entry in the relevant guild shall default to permission tier 0.
- **FR-19**: Commands without a `CommandMetaData` database entry in the relevant guild shall default to required tier 0.
- **FR-20**: Permission tiers shall be stored per guild, per user (keyed by guild ID and Discord ID) and per command (keyed by guild ID and command name) in the PostgreSQL database.
- **FR-21**: Permission tiers shall be adjustable at runtime, without code changes or restart, via chat commands:
  - `!commandpermission <commandName> [tier]`
    - With only a command name: report that command's current tier (0 if unknown).
    - With a tier: create or update the command's tier and persist it.
  - `!userpermission <user> [tier]`
    - With only a user: report that user's current tier (0 if unknown).
    - With a tier: create or update the user's tier and persist it.
- **FR-22**: The `!userpermission` command shall accept a Discord mention (`<@id>` / `<@!id>`) as well as a raw numeric Discord ID as the user argument.
- **FR-23**: The permission-management commands themselves are subject to the same permission checks as any other command (their own tiers can be raised via `!commandpermission`).
- **FR-24**: The bot shall support membership in multiple guilds simultaneously, with all persisted state (user tiers, command metadata, settings) scoped per guild. A user's tier or a command's required tier in one guild shall have no effect in any other guild.
- **FR-25**: Commands received outside a guild (direct messages) shall run in a defined **DM scope**: the invoking user holds owner tier (the configured bot owner tier 4), commands require their built-in default permission tiers, and no per-guild state shall be read or written — permissions cannot be adjusted from direct messages. Commands that manage per-server state (`!userpermission`, `!commandpermission`, `!settings`) shall decline in direct messages with a clear in-channel message.
- **FR-26**: Bot configuration options (e.g. the command identifier) shall be configurable per guild, with the global `appsettings.json` values acting as defaults for guilds without explicit settings.
- **FR-33**: Named tier levels shall be defined for the permission scale (e.g. 0 = everyone, 1 = moderator, 2 = admin, 3 = owner) and documented, so commands and users can be assigned roles by name rather than by arbitrary numbers.
- **FR-34**: Each guild shall have a bootstrap administrator: the guild owner shall automatically hold the highest guild-level tier (owner) in that guild, without any prior database setup, so administration is possible from the first message. (The globally highest tier, bot owner, is defined by FR-41; there is no separate configured-admin id — the bot owner supersedes it.)
- **FR-35**: The permission-management commands (`!commandpermission`, `!userpermission`) shall have a built-in default required tier of admin (see FR-33), overriding the general default of FR-19 for these commands, so unprivileged users cannot self-promote on a fresh guild.
- **FR-36 (escalation guard)**: A user may only assign permission tiers strictly lower than their own tier. A user shall not be able to modify their own tier or the tier of a user whose tier is equal to or higher than their own.
- **FR-37**: Assigned tiers shall be validated: integers within the defined tier scale (non-negative, not exceeding the highest defined tier). Invalid input shall produce an in-channel error message.
- **FR-38**: A `!help` command shall list the commands available to the invoking user (i.e. commands whose required tier in the current guild is at most the user's tier), so users and staff can discover what they can run.
- **FR-39**: Each registered command shall expose help text: a short description and a usage/argument summary, supplied with the command implementation (see FR-12) so it stays in sync with the code.
- **FR-40**: Invoking `!help <commandName>` shall display the detailed help text (description, usage, required permission tier) for that command. Detailed help shall only be shown for commands the invoking user can run; requesting help for an unavailable or unknown command shall produce an in-channel message saying so, without revealing whether the command exists at a higher tier.
- **FR-41 (bot owner)**: A bot owner Discord ID shall be configurable via settings (`Permissions:BotOwnerDiscordId`; `BOT_OWNER_DISCORD_ID` in the container `.env` file). The bot owner shall automatically hold the highest permission tier of all (bot owner, above the guild-level tiers of FR-33) in every guild, and shall be the designated authority for features that apply across guilds (global settings and defaults). The bot owner tier shall be configuration-only and can never be assigned through the permission-management commands.

### 2.5 Persistence & startup

- **FR-27**: User permission data shall be persisted across restarts in PostgreSQL via EF Core (`User` entity keyed by guild ID and `DiscordId`).
- **FR-28**: Command permission metadata shall be persisted across restarts in PostgreSQL via EF Core (`CommandMetaData` entity keyed by guild ID and `CommandName`).
- **FR-29**: The database connection string shall be supplied via configuration (`ConnectionStrings:DefaultConnection`), kept outside source control.
- **FR-30**: The bot shall apply EF Core database migrations automatically on startup.
- **FR-31**: If the database is unreachable at startup, the bot shall fail fast with a clear error message and a non-zero exit code, instead of starting in a degraded state.
- **FR-32**: Missing or invalid required configuration keys (`Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, `BotConfiguration:BotName`, `BotConfiguration:LogFormat`, `BotConfiguration:CommandIdentifier`) shall be detected at startup, before any connection attempt, and reported with an explicit error naming the offending key.

### 2.6 Quotes (per guild)

- **FR-42**: The bot shall store quotes scoped per guild: a `Quote` entity shall be persisted via EF Core in PostgreSQL (covered by the automatic startup migrations, FR-30), recording the quote text, the Discord ID of the user who added it, and the timestamp of the adding message. A quote created in one guild shall never be visible, searchable, or removable in any other guild (per-guild scoping, FR-24).
- **FR-43**: Each quote shall carry a per-guild number that acts as a simple sequential index, **0-based like the old bot's index-based quote list (the first quote is number 0)**: every new quote is assigned one greater than the guild's current maximum number, so the number increases by one with each quote added. Removing a quote shall never change the numbers of the remaining quotes.
- **FR-44**: `!quote` with no arguments shall reply in-channel with a random quote from the invoking guild, prefixed with its number. If the invoking guild has no quotes, the bot shall reply with a friendly "no quotes yet" message instead of an error.
- **FR-45**: `!quote <number>` shall reply in-channel with that exact quote, prefixed with its number. An unknown number shall produce an in-channel error message, never an exception or an empty reply. An argument that does not parse as a number (including numeric-looking values outside the representable range) shall be treated as quote text to add (see FR-47).
- **FR-46**: `!searchquotes <text...>` shall reply in-channel with the invoking guild's quotes whose text contains the search string, matched case-insensitively, each result prefixed with its number, capped at 10 results. If no quotes match, the bot shall reply with a "no matching quotes" message. Empty search text shall be rejected with an in-channel error.
- **FR-47**: `!quote <text...>` — any argument that is not a number — shall add the text as a new quote in the invoking guild (quoting is a single, immediate action) and reply with the assigned number. Quote text shall be capped at 2000 characters (Discord's message limit); longer text shall be rejected with an in-channel error. When a quote reply — a maximal quote plus its number prefix, or a search result list of up to 10 quotes (FR-44–FR-46) — would exceed Discord's message limit, the reply shall be trimmed to fit with a trailing ellipsis; the stored text shall never be modified.
- **FR-48**: `!removequote <number>` shall remove that quote from the invoking guild and confirm in-channel; an unknown number shall produce an in-channel error. `!removequote` shall have a built-in default required tier of moderator (FR-33), overriding the general default of FR-19; `quote` (both fetching and adding) and `searchquotes` shall default to tier 0 (everyone).
- **FR-49**: Quote commands shall be subject to the standard command pipeline behavior: runtime permission adjustment via `!commandpermission` (FR-21), per-tier cooldown throttling (NFR-12), typed argument parsing through the shared mechanism (NFR-13), help text (FR-39, FR-40), and lowercase names (C-4).
- **FR-50**: Quote commands manage per-guild state and shall therefore decline in direct messages with the established "only available in servers" message (consistent with FR-25).

### 2.7 Custom command responses (per guild)

- **FR-51**: The bot shall support user-defined text responses scoped per guild: a `CustomResponse` entity shall be persisted via EF Core in PostgreSQL (covered by the automatic startup migrations, FR-30), recording the response name, its content, the Discord ID of the user who last modified it, and created/updated timestamps. A response defined in one guild shall have no effect in any other guild (per-guild scoping, FR-24).
- **FR-52**: Response names shall be a single lowercase token of 1–32 characters, consisting only of lowercase letters, digits, hyphens, and underscores. Invalid names shall be rejected with an in-channel error.
- **FR-53**: A response shall not take the name of a registered built-in command: creation shall reject such names with an in-channel error. Built-in commands shall always win dispatch — a custom response can never shadow a built-in, including built-ins added later.
- **FR-54**: When a command token matches no built-in command, the bot shall look up the invoking guild's custom responses before replying "unavailable"; a match shall reply in-channel with the stored content.
- **FR-55**: Custom response invocations shall go through the same checks as built-in commands: the per-(guild, user, command) cooldown throttle keyed on the response name (NFR-12), and a permission check defaulting to tier 0 whose required tier shall be adjustable at runtime via `!commandpermission` (FR-21), stored as regular `CommandMetaData` (FR-19, FR-28).
- **FR-56**: `!addresponse <name> <content...>` shall create the response, or — if the name already exists in the guild — update its content and modification fields; the reply shall distinguish "created" from "updated". Empty content shall be rejected with an in-channel error; content shall be capped at 1000 characters. The command shall have a built-in default required tier of moderator (FR-33), overriding the general default of FR-19.
- **FR-57**: `!removeresponse <name>` shall delete that response from the guild and confirm in-channel; an unknown name shall produce an in-channel error. The command shall have a built-in default required tier of moderator (FR-33), overriding the general default of FR-19.
- **FR-58**: `!listresponses` shall list the invoking guild's response names alphabetically; a guild with no responses shall get a message saying so. The command shall default to tier 0 (everyone).
- **FR-59**: Custom responses shall not apply in direct messages (they are guild state): in DMs an unknown token shall keep the normal "unavailable" reply. The response-management commands (`!addresponse`, `!removeresponse`, `!listresponses`) manage per-guild state and shall decline in direct messages with the established "only available in servers" message (consistent with FR-25).
- **FR-60**: A custom response shall consist of text content only. File or image attachments on the `!addresponse` invocation shall be neither stored nor replayed — such invocations shall be rejected with an in-channel error — and a dispatched response shall never include an uploaded file. Links in the content (e.g. `!addresponse <name> <image link>`, the expected most common use case) are ordinary text: they are stored verbatim and sent as a plain text reply, with any link preview or embed rendered by Discord client-side. The bot shall upload no files itself.

### 2.8 Reminders (user-scoped)

- **FR-61**: The bot shall support timed reminders scoped per user: a `Reminder` entity shall be persisted via EF Core in PostgreSQL (covered by the automatic startup migrations, FR-30), recording the reminder message, the due time in UTC, the owner's Discord ID, and the guild ID of creation (null when created in a direct message). Reminders are personal (user-scoped, not guild-scoped) and work from both guild channels and direct messages.
- **FR-62**: `!remindme <duration> <message...>` shall parse the duration from leading number+unit tokens: units `s`, `m`, `h`, `d`, `w` with long forms (`seconds` … `weeks`) accepted, compact (`10d 5h`) and spaced (`10 d 5 h`) forms both valid, and multiple tokens summed (e.g. `!remindme 1d 12h water the plants`). The message shall be the remainder of the invocation, non-empty and capped at 500 characters.
- **FR-63**: The total duration shall be positive — there shall be no minimum or maximum bound: durations of years are accepted. Anything unparsable or non-positive shall produce an in-channel error with a usage hint, and nothing shall be stored.
- **FR-64**: On success the reminder shall be persisted (FR-61) and the bot shall confirm in-channel, echoing the message and the due time.
- **FR-65**: A hosted background service shall deliver each due reminder as a direct message to the owner containing the reminder message, second-accurate: it shall schedule its next wake-up at the earliest pending due time instead of polling on a fixed coarse interval, re-scheduling when a newly created (or cancelled) reminder becomes the earliest, with a bounded fallback poll (every 10 seconds) to cover clock drift and missed wake-ups. Delivered reminders shall be removed from the pending set. Delivery shall never block or crash the service.
- **FR-66**: Reminders shall survive restarts: the pending set is persisted, and a reminder that became due while the bot was down shall be delivered once the service starts.
- **FR-67**: A delivery failure (e.g. closed DMs) shall be retried a bounded number of times and then marked failed and logged; it shall never block other reminders and never produce a user-facing crash.
- **FR-68**: `!reminders` shall list the invoking user's pending reminders (number, due time, message), soonest first, capped at 10 entries.
- **FR-69**: `!cancelreminder <number>` shall cancel one of the invoking user's own pending reminders (numbers from `!reminders`) and confirm; users shall never be able to see or cancel another user's reminders. Unknown or foreign numbers shall produce an in-channel error.
- **FR-70**: A per-user cap of 10 pending reminders shall be enforced; exceeding it shall produce an in-channel error instead of storing the reminder.
- **FR-71**: The reminder commands (`!remindme`, `!reminders`, `!cancelreminder`) shall default to tier 0 (everyone) and be subject to the standard command pipeline behavior: per-tier cooldown throttling (NFR-12) and help text (FR-39, FR-40). Because reminders are user-scoped, not guild-scoped, all three shall remain fully usable in direct messages (DM scope, FR-25).

### 2.9 Quote import (legacy migration)

- **FR-72**: The bot owner shall be able to import the old bot's quotes into a guild: `!importquotes`, run in the target guild with the old bot's `quoteSettings.json` attached as a file to the invocation message. The command shall download the attachment from its Discord CDN URL and parse the legacy format — a JSON object with a `quotes` array whose entries carry a `quote` text and an optional `date` timestamp.
- **FR-73**: Imported quotes shall be appended to the invoking guild in file order, continuing the per-guild sequential numbering (FR-43). Each imported quote shall record the Discord ID of the importing user as `AddedByDiscordId` and shall take its `CreatedAt` from the legacy entry's `date` when present, falling back to the import message's timestamp otherwise.
- **FR-74**: The import shall be all-or-nothing: an unreadable or malformed attachment, a file containing no quotes, or any entry whose text is empty or longer than 2000 characters (FR-47) shall produce an in-channel error naming the problem, and nothing shall be stored. The file itself has no size or quote-count limit.
- **FR-75**: `!importquotes` shall have a built-in default required tier of **bot owner (tier 4)** — a tier reachable only through configuration (FR-37), so only the configured bot owner can ever run it. It is subject to the standard command pipeline behavior (FR-49) and, because it writes guild state, declines in direct messages (FR-50).

## 3. Non-Functional Requirements

- **NFR-1 Platform**: The bot targets .NET 10 (`net10.0`) and runs as a long-lived worker service (generic host).
- **NFR-2 Configurability**: The bot name, log format, and command identifier are configurable via `appsettings.json` without code changes.
- **NFR-3 Secret handling**: The Discord token and database connection string must never be committed to the repository (`appsecrets.json` is gitignored and loaded at startup for local/source runs).
- **NFR-4 Secret-free images**: Built Docker images shall contain no secrets: containerized deployments shall receive secrets via environment variables (`Secrets__BotToken`, `ConnectionStrings__DefaultConnection`), and `appsecrets.json` shall be excluded from the publish output.
- **NFR-5 Testability**: Core command infrastructure, command naming, and permission logic shall be covered by unit tests that run without a live database or Discord connection (MSTest + Moq, in-memory configuration). The error-handling failure path and startup validation logic shall also be unit-tested.
- **NFR-6 Build & CI**: The solution shall build with `dotnet build HambotV2.sln` and tests shall run with `dotnet test HambotV2.sln` on the .NET 10 SDK. Continuous integration is provided by an existing **Jenkins server**; the Jenkins pipeline shall build and test the solution on push and must be kept up to date when the build/test setup changes.
- **NFR-7 Warning-free build**: The build shall complete without nullable warnings (CS8625, CS8604, CS8620, CS0162), enabling CI to enforce warnings-as-errors.
- **NFR-8 Deployability**: The bot shall be deployable as a Docker container built from `HambotV2/Dockerfile` (repository-root build context). Given valid secrets, the container shall reach "Gateway: Ready".
- **NFR-9 Extensibility**: New commands shall be addable by implementing `ICommand` and registering it in the appropriate `Register*Commands` class, without modifying the command pipeline.
- **NFR-10 Resource isolation**: Command handling shall use per-message, transient services (handler, permission service, database context), disposed after each message.
- **NFR-11 Audit logging**: Command executions (user, command, outcome) shall be traceable from logs without debugging.
- **NFR-12 Rate limiting**: Command spam shall be throttled per user/command with a user-friendly response. The cooldown window shall be configurable per permission tier, with global per-tier defaults in `appsettings.json` and per-guild overrides adjustable at runtime via a chat command (design in [`software-design.md`](software-design.md) §3.2).
- **NFR-13 Argument parsing**: Commands shall receive typed, validated arguments through a shared parsing mechanism instead of hand-rolled `Split`/`TryParse` code.
- **NFR-14 Quotes testability**: The quotes feature shall be covered by unit tests that run without a live database or Discord connection (MSTest + Moq, EF InMemory), including: add/get/remove lifecycle with sequential numbering (including number reuse when the highest-numbered quote is removed), per-guild isolation, random fetch on a populated guild and the empty-guild message, fetch-by-number, add-by-text (including a numeric-looking argument that is out of range), text validation at the 2000-character boundary and the reply trimming of over-limit fetches and search results, search behavior and its result cap, permission gating, and DM declines.
- **NFR-15 Custom-response testability**: The custom-response feature shall be covered by unit tests that run without a live database or Discord connection, including: name/content validation, built-in collision rejection, create-vs-update behavior, removal, per-guild isolation, dispatch order (built-in wins over custom, custom wins over "unavailable"), cooldown and permission gating of response dispatch, attachment rejection (FR-60), and DM behavior.
- **NFR-16 Reminders testability**: The reminders feature shall be covered by unit tests that run without a live database or Discord connection (using `FakeTimeProvider` and EF InMemory), including: duration parsing (units, long/compact/spaced forms, sums, invalid input, non-positive durations, multi-year durations), message validation, confirmation reply, per-user isolation and caps, list and cancel behavior, due-vs-not-due delivery as time advances (including re-scheduling when an earlier reminder is added), restart recovery (a persisted due reminder is delivered when the service starts), and the bounded-failure path.

## 4. Constraints

- **C-1**: A valid Discord bot token is required to run the bot (login occurs at startup).
- **C-2**: A reachable PostgreSQL instance is required for startup (migrations, per FR-27/FR-28) and for any command that touches persistence (including permission checks and the permission-management commands); connection defaults assume `host.docker.internal:5432` when containerized.
- **C-3**: The Message Content intent must be enabled for the bot application in the Discord developer portal for command parsing to work.
- **C-4**: Command names must be lowercase; lookups lowercase the input token.

## 5. Command Interface

| Invocation | Arguments | Effect |
|---|---|---|
| `!hello` | none | Bot replies "Hello World". |
| `!help` | none | Lists the commands the invoking user can run in this guild. |
| `!help <name>` | command name | Shows the detailed help text (description, usage, required tier) for that command, if the user can run it. |
| `!commandpermission <name>` | command name | Prints the command's permission tier (0 if not set). |
| `!commandpermission <name> <tier>` | command name, integer tier | Sets or creates the command's permission tier. |
| `!userpermission <user>` | Discord mention or numeric Discord ID | Prints the user's permission tier (0 if not set). |
| `!userpermission <user> <tier>` | Discord mention or numeric Discord ID, integer tier | Sets or creates the user's permission tier. |
| `!settings` | none | Shows the guild's effective command identifier and per-tier cooldown overrides. |
| `!settings commandidentifier <value>` | identifier string | Sets the guild's command identifier override (≤ 5 chars, non-empty). |
| `!settings commandidentifier reset` | — | Clears the guild's identifier override back to the global default. |
| `!settings cooldown <tier> <seconds>` | integer tier (0–3), integer seconds | Sets the guild's per-tier cooldown override (`0` disables throttling for that tier). |
| `!settings cooldown <tier> reset` | integer tier (0–3) | Clears the guild's cooldown override back to the global default. |
| `!quote` | none | Replies with a random quote from this guild, prefixed with its number; a guild with no quotes gets a "no quotes yet" message. |
| `!quote <number>` | integer | Replies with that exact quote; an unknown number produces an in-channel error. |
| `!quote <text...>` | quote text | Any argument that is not a number adds the text as a new quote and replies with its assigned number; text over 2000 characters is rejected in-channel. |
| `!searchquotes <text...>` | search text | Lists this guild's quotes whose text contains the search string (case-insensitive), number-prefixed, capped at 10 results. |
| `!removequote <number>` | integer | Removes that quote from this guild and confirms; an unknown number produces an in-channel error. Default required tier: moderator. |
| `!importquotes` | legacy `quoteSettings.json` attached to the message | Imports the old bot's quotes into this guild (file order, continuing numbering, legacy dates preserved). Any failure — no attachment, unreadable/malformed file, no quotes, an entry that is empty or over 2000 characters — is an in-channel error and stores nothing. Default required tier: bot owner. |
| `!addresponse <name> <content...>` | name (1–32 chars: lowercase letters, digits, `-`, `_`), content text | Creates the response, or updates its content if the name already exists in this guild; the reply says "created" or "updated". Empty content and content over 1000 characters are rejected in-channel. Content is text only — including links (e.g. an image link, rendered as an embed by Discord); invocations carrying file/image attachments are rejected in-channel (FR-60). Default required tier: moderator. |
| `!removeresponse <name>` | response name | Deletes that response from this guild and confirms; an unknown name produces an in-channel error. Default required tier: moderator. |
| `!listresponses` | none | Lists this guild's response names alphabetically; a guild with none gets a message saying so. |
| `!remindme <duration> <message...>` | duration tokens (`s`, `m`, `h`, `d`, `w`, with long forms; compact `10d 5h` and spaced `10 d 5 h`, multiple tokens summed), message text | Schedules a personal reminder and confirms in-channel, echoing the message and due time. No minimum or maximum duration (years are fine); the message is capped at 500 characters. Works in DMs. |
| `!reminders` | none | Lists the invoking user's pending reminders (number, due time, message), soonest first, capped at 10 entries. |
| `!cancelreminder <number>` | integer | Cancels one of the invoking user's own pending reminders and confirms; unknown or foreign numbers produce an in-channel error. |

Argument errors (unparseable tier, unparseable user/ID, too few arguments) produce an in-channel error message. The quote and response-management commands manage per-guild state and decline in direct messages with the "only available in servers" message (FR-50, FR-59). An unknown command token is checked against the invoking guild's custom responses — subject to the same cooldown and permission checks — before the "unavailable" reply (FR-10, FR-54); custom responses do not apply in direct messages. The reminder commands are user-scoped and remain fully usable in direct messages (FR-71).

## 6. Out of Scope

The following are explicitly not provided:

- Slash commands / Discord application commands (only message-based `!` commands).
- Per-channel configuration (per-guild configuration is in scope, see FR-24 to FR-26).
- Usage analytics.
- Uploaded file or image attachments in custom responses (content is text only — links, which Discord may render as embeds, are fine; FR-60).
- A web interface or external admin tooling (permissions are managed via chat commands only).
