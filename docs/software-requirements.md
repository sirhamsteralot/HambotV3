# Software Requirements

This document captures the requirements for HambotV2, derived from the implemented system, its tests, repository conventions, and the roadmap. It describes the software as it *should be*: items may not all be satisfied by the current implementation yet (see [`roadmap.md`](roadmap.md) for scheduling); roadmap tasks reference the requirement IDs defined here.

## 1. Product Description

HambotV2 ("HamBot") is a Discord chat bot. It listens to messages in the guilds the bot account is a member of, recognizes messages that start with a configurable command identifier (default `!`), and executes registered commands. Command access is governed by a tiered permission system persisted in PostgreSQL and scoped per Discord guild.

## 2. Functional Requirements

### 2.1 Discord connectivity

- **FR-1**: The bot shall connect to the Discord gateway using a bot token supplied via configuration (`Secrets:BotToken`), kept outside source control (see NFR-3).
- **FR-2**: The bot shall receive all unprivileged gateway events plus message content (`GatewayIntents.AllUnprivileged | MessageContent`).
- **FR-3**: The bot shall log Discord gateway events to the host logger using a configurable log format (`BotConfiguration:LogFormat`).
- **FR-4**: The bot shall log its gateway connection state periodically (every 5 minutes) while running.
- **FR-5**: The bot shall run continuously as a background worker service until cancelled.
- **FR-6 (roadmap T8)**: On host shutdown the bot shall log out and dispose the Discord client cleanly; gateway disconnects and reconnects shall be logged.

### 2.2 Command recognition

- **FR-7**: The bot shall treat any received message starting with the configured command identifier (`BotConfiguration:CommandIdentifier`, default `!`) as a command invocation.
- **FR-8**: The command name shall be the first space-delimited token of the message, with the identifier stripped and the remainder lowercased before lookup.
- **FR-9**: Degenerate command invocations — identifier-only messages (e.g. a bare `!`), whitespace-only content, or empty tokens — shall be handled safely, without exceptions (roadmap T5).
- **FR-10**: If the command name is not registered, the bot shall reply in-channel that the requested command is unavailable.
- **FR-11**: Registered commands shall be looked up by name; command names must be lowercase (enforced by unit tests).

### 2.3 Command execution & error handling

- **FR-12**: Each command shall be a class implementing `ICommand` (`Name` + `ExecuteCommand(SocketMessage, ICommandContext)`), registered at startup through the command registration chain. Commands shall also provide their help text (FR-39) as part of the implementation.
- **FR-13**: A command shall receive an execution context wrapping a database context scoped to the message being handled.
- **FR-14**: If the invoking user lacks sufficient permissions, the bot shall reply in-channel that the user is not permitted to use the command, and the command shall not execute.
- **FR-15 (roadmap T1)**: A failure in a command, permission check, or database access shall produce a user-facing error message in-channel and a logged exception; it shall never stop the bot from processing subsequent messages. Exception details shall not be exposed in-channel.
- **FR-16 (roadmap T1)**: Per-message resources (command handler, database context) shall always be released after handling a message, including on the failure path.

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
- **FR-22 (roadmap T7)**: The `!userpermission` command shall accept a Discord mention (`<@id>` / `<@!id>`) as well as a raw numeric Discord ID as the user argument.
- **FR-23**: The permission-management commands themselves are subject to the same permission checks as any other command (their own tiers can be raised via `!commandpermission`).
- **FR-24 (roadmap T14)**: The bot shall support membership in multiple guilds simultaneously, with all persisted state (user tiers, command metadata, settings) scoped per guild. A user's tier or a command's required tier in one guild shall have no effect in any other guild.
- **FR-25 (roadmap T14)**: Commands received outside a guild (direct messages) shall be handled by a defined fallback (rejected with a clear message, or evaluated against a global default scope) to be settled in the per-guild design.
- **FR-26 (roadmap T14)**: Bot configuration options (e.g. the command identifier) shall be configurable per guild, with the global `appsettings.json` values acting as defaults for guilds without explicit settings.
- **FR-33 (roadmap T16)**: Named tier levels shall be defined for the permission scale (e.g. 0 = everyone, 1 = moderator, 2 = admin, 3 = owner) and documented, so commands and users can be assigned roles by name rather than by arbitrary numbers.
- **FR-34 (roadmap T16)**: Each guild shall have a bootstrap administrator: the guild owner shall automatically hold the highest guild-level tier (owner) in that guild, without any prior database setup, so administration is possible from the first message. (The globally highest tier, bot owner, is defined by FR-41; there is no separate configured-admin id — the bot owner supersedes it.)
- **FR-35 (roadmap T16)**: The permission-management commands (`!commandpermission`, `!userpermission`) shall have a built-in default required tier of admin (see FR-33), overriding the general default of FR-19 for these commands, so unprivileged users cannot self-promote on a fresh guild.
- **FR-36 (roadmap T16 — escalation guard)**: A user may only assign permission tiers strictly lower than their own tier. A user shall not be able to modify their own tier or the tier of a user whose tier is equal to or higher than their own.
- **FR-37 (roadmap T16)**: Assigned tiers shall be validated: integers within the defined tier scale (non-negative, not exceeding the highest defined tier). Invalid input shall produce an in-channel error message.
- **FR-38 (roadmap T17)**: A `!help` command shall list the commands available to the invoking user (i.e. commands whose required tier in the current guild is at most the user's tier), so users and staff can discover what they can run.
- **FR-39 (roadmap T17)**: Each registered command shall expose help text: a short description and a usage/argument summary, supplied with the command implementation (see FR-12) so it stays in sync with the code.
- **FR-40 (roadmap T17)**: Invoking `!help <commandName>` shall display the detailed help text (description, usage, required permission tier) for that command. Detailed help shall only be shown for commands the invoking user can run; requesting help for an unavailable or unknown command shall produce an in-channel message saying so, without revealing whether the command exists at a higher tier.
- **FR-41 (bot owner)**: A bot owner Discord ID shall be configurable via settings (`Permissions:BotOwnerDiscordId`; `BOT_OWNER_DISCORD_ID` in the container `.env` file). The bot owner shall automatically hold the highest permission tier of all (bot owner, above the guild-level tiers of FR-33) in every guild, and shall be the designated authority for features that apply across guilds (global settings and defaults). The bot owner tier shall be configuration-only and can never be assigned through the permission-management commands.

### 2.5 Persistence & startup

- **FR-27**: User permission data shall be persisted across restarts in PostgreSQL via EF Core (`User` entity keyed by guild ID and `DiscordId`).
- **FR-28**: Command permission metadata shall be persisted across restarts in PostgreSQL via EF Core (`CommandMetaData` entity keyed by guild ID and `CommandName`).
- **FR-29**: The database connection string shall be supplied via configuration (`ConnectionStrings:DefaultConnection`), kept outside source control.
- **FR-30 (roadmap T2)**: The bot shall apply EF Core database migrations automatically on startup.
- **FR-31 (roadmap T2)**: If the database is unreachable at startup, the bot shall fail fast with a clear error message and a non-zero exit code, instead of starting in a degraded state.
- **FR-32 (roadmap T4)**: Missing or invalid required configuration keys (`Secrets:BotToken`, `ConnectionStrings:DefaultConnection`, `BotConfiguration:BotName`, `BotConfiguration:LogFormat`, `BotConfiguration:CommandIdentifier`) shall be detected at startup, before any connection attempt, and reported with an explicit error naming the offending key.

## 3. Non-Functional Requirements

- **NFR-1 Platform**: The bot targets .NET 10 (`net10.0`) and runs as a long-lived worker service (generic host).
- **NFR-2 Configurability**: The bot name, log format, and command identifier are configurable via `appsettings.json` without code changes.
- **NFR-3 Secret handling**: The Discord token and database connection string must never be committed to the repository (`appsecrets.json` is gitignored and loaded at startup for local/source runs).
- **NFR-4 Secret-free images (roadmap T3)**: Built Docker images shall contain no secrets: containerized deployments shall receive secrets via environment variables (`Secrets__BotToken`, `ConnectionStrings__DefaultConnection`), and `appsecrets.json` shall be excluded from the publish output.
- **NFR-5 Testability**: Core command infrastructure, command naming, and permission logic shall be covered by unit tests that run without a live database or Discord connection (MSTest + Moq, in-memory configuration). The error-handling failure path (roadmap T1) and startup validation logic (roadmap T4) shall also be unit-tested.
- **NFR-6 Build & CI**: The solution shall build with `dotnet build HambotV2.sln` and tests shall run with `dotnet test HambotV2.sln` on the .NET 10 SDK. Continuous integration is provided by an existing **Jenkins server**; the Jenkins pipeline shall build and test the solution on push and must be kept up to date when the build/test setup changes (roadmap T10).
- **NFR-7 Warning-free build (roadmap T11)**: The build shall complete without nullable warnings (CS8625, CS8604, CS8620, CS0162), enabling CI to enforce warnings-as-errors.
- **NFR-8 Deployability**: The bot shall be deployable as a Docker container built from `HambotV2/Dockerfile` (repository-root build context). Given valid secrets, the container shall reach "Gateway: Ready".
- **NFR-9 Extensibility**: New commands shall be addable by implementing `ICommand` and registering it in the appropriate `Register*Commands` class, without modifying the command pipeline.
- **NFR-10 Resource isolation**: Command handling shall use per-message, transient services (handler, permission service, database context), disposed after each message.
- **NFR-11 Audit logging (roadmap T12)**: Command executions (user, command, outcome) shall be traceable from logs without debugging.
- **NFR-12 Rate limiting (roadmap T13)**: Command spam shall be throttled per user/command with a user-friendly response.
- **NFR-13 Argument parsing (roadmap T15)**: Commands shall receive typed, validated arguments through a shared parsing mechanism instead of hand-rolled `Split`/`TryParse` code.

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

Argument errors (unparseable tier, unparseable user/ID, too few arguments) produce an in-channel error message.

## 6. Out of Scope

The following are explicitly not provided:

- Slash commands / Discord application commands (only message-based `!` commands).
- Per-channel configuration (per-guild configuration is in scope, see FR-24 to FR-26).
- Usage analytics.
- A web interface or external admin tooling (permissions are managed via chat commands only).
