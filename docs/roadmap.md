# Roadmap

This roadmap defines the tasks to pick up next for HambotV2. It is grouped into phases; within each phase, tasks can largely be picked up independently unless a dependency is noted. Each task lists what needs to be done and acceptance criteria that define "done".

The first two phases address the limitations recorded in [`software-design.md`](software-design.md) (§8) and additional shortcomings needed for a solid, deployable Discord bot base.

## Phase 1 — Stability & Correctness (current limitations)

### T1. [x] Error handling in the command pipeline

**Problem**: Exceptions thrown inside a command, the permission check, or the database are not caught at the message-processing level. A failing command surfaces as an unhandled Discord event handler exception; the user gets no feedback and the handler's disposal behavior is not guaranteed on the failure path.

**Work**:
- Wrap command execution (and the permission check) in `CommandHandler.HandleCommand` with a try/catch.
- On failure: log the exception with the message content and command name, and reply in-channel with a generic error message (do not leak exception details to Discord).
- Ensure the per-message `CommandHandler` (and its `DatabaseContext`) is always disposed, also on the failure path.
- Decide and document behavior for repeat failures (e.g. a single failed message must never take the bot down).

**Acceptance criteria**:
- A command that throws results in an in-channel error message, not an unhandled exception in logs.
- A database outage during a permission check produces a user-facing error and the bot keeps processing subsequent messages.
- Unit tests cover the failure path of `CommandHandler` (command throws, permission service throws).

### T2. [x] Database initialization (migrations at startup)

**Problem**: Migrations exist (`HambotV2/Migrations/`, initial create only) but are never applied automatically. The database must be prepared manually out-of-band, otherwise every command that touches persistence fails.

**Work**:
- Apply EF Core migrations on startup (e.g. `db.Database.Migrate()` in `Program.cs` or in the `DiscordClient` startup before the gateway login), with a clear log line on success/failure.
- Fail fast with a clear log message if the database is unreachable, instead of starting a half-functional bot.

**Acceptance criteria**:
- Starting the bot against an empty database creates the schema automatically.
- If the database is unreachable, startup logs a clear error and the host exits with a non-zero code.
- Documented in the README (see T9) that no manual migration step is required.

### T3. [x] Secrets in the Docker image

**Problem**: `COPY . .` in the Docker build stage copies everything under `HambotV2/` matching publish rules — including the gitignored `appsecrets.json` — into the published image. The image cannot be pushed to any shared registry without leaking the bot token and database password.

**Work**:
- Move secrets out of the baked-in file: support environment variables (`Secrets__BotToken`, `ConnectionStrings__DefaultConnection`) as the primary configuration source in containers (the .NET configuration stack already supports `__` env var overrides).
- Exclude `appsecrets.json` from the publish output (`.dockerignore` and/or publish exclusion) so the image is secret-free; keep local-dev file loading as a fallback.
- Optionally add a `.dockerignore` covering `TestResults/`, `obj/`, `bin/` etc. to slim the build context.

**Acceptance criteria**:
- The built image contains no `appsecrets.json` and no token/password strings.
- The container runs correctly with secrets provided via environment variables.
- Documented in the README (see T9).

### T4. [x] Startup configuration validation (fail fast)

**Problem**: A missing `Secrets:BotToken` or connection string is only discovered at login/connection time with unclear errors. `LogFormat` is read with `!` and would throw a startup `NullReferenceException` if absent.

**Work**:
- Validate required configuration at startup (`BotToken`, `DefaultConnection`, `BotName`, `LogFormat`, `CommandIdentifier`) before any connection attempt.
- Exit with a clear log message listing which keys are missing/invalid.

**Acceptance criteria**:
- Starting without a bot token fails immediately with an explicit message naming the missing key.
- Unit tests cover the validation logic.

### T5. [x] Command parsing robustness

**Problem**: `DiscordClient` forwards any message starting with `!` (including a bare `!`), and `CommandService.TryGetCommand` assumes the first token has at least one character after the identifier — a bare identifier message can throw. Identifier comparison is case-sensitive and trimmed only partially.

**Work**:
- Make `TryGetCommand`/`HandleCommand` safely reject: bare identifier-only messages, empty tokens, and whitespace variations.
- Decide on trimming/casing behavior for the identifier and document it.

**Acceptance criteria**:
- A bare `!` message is ignored (or answered) without exceptions.
- Unit tests cover identifier-only and whitespace-only messages.

### T6. [x] Clean up the persistence abstraction

**Problem**: `Connections/IDatabaseContext.cs` exists but is unused — everything injects the concrete `DatabaseContext`. This blocks unit-testing commands against a fake context and leaves dead code.

**Work**:
- Either remove `IDatabaseContext` entirely, or finish it: move the `DbSet` properties onto the interface and have `CommandHandler`/`CommandContext` depend on it.
- Prefer the abstraction route if it lets the `CommandPermission`/`UserPermission` commands be tested without a database.

**Acceptance criteria**:
- No dead abstraction remains; the choice is applied consistently.
- (If kept) permission-management commands have unit tests using a mocked/fake context.

**Note**: schedule before T16 and T7 — the mocked context is what makes the permission commands' behavior unit-testable without a database.

### T16. [x] Permission bootstrap, named role model, and escalation guard

**Problem**: On a fresh guild every user and command defaults to tier 0 (FR-18/FR-19), which means *anyone* can run `!userpermission` and `!userpermission @themselves <max tier>` — permission management is effectively open until someone locks it, and there is no defined way for the first admin to exist. Tiers are also anonymous integers with no defined meaning, and there is no guard against privilege escalation.

**Work** (implements FR-33 to FR-37 in [`software-requirements.md`](software-requirements.md)):
- Define the named tier scale (e.g. 0 = everyone, 1 = moderator, 2 = admin, 3 = owner) and document it in the README and `software-design.md`.
- Implement the bootstrap administrator: guild owner and/or a settings-configured Discord ID automatically hold the highest tier (FR-34).
- Give the permission-management commands a built-in default required tier of admin (FR-35).
- Implement the escalation guard in the permission commands: users may only assign tiers strictly below their own, and cannot modify their own tier or that of equal/higher-tiered users (FR-36).
- Validate tier inputs against the defined scale (FR-37).

**Acceptance criteria**:
- On a fresh guild, a random user cannot grant themselves (or anyone) a tier; the guild owner / configured admin can.
- A moderator cannot promote a user to admin or modify an admin's tier.
- Tier assignments outside the defined scale are rejected with an in-channel error.
- All of the above is unit-tested.

**Note**: pick up after T6 (testability) and before T7/T17 — both build on the tier model this task defines.

### T7. [x] Permission-management command UX

**Problem**: `!userpermission` requires a raw numeric Discord ID. In practice, admins mention users (`@user`), which produces `<@123456>` and fails to parse. There is also dead code (unreachable `return`) in `UserPermission`.

**Work**:
- Accept both raw IDs and Discord mentions (`<@id>` / `<@!id>`) as the user argument.
- Remove the unreachable code and share the "upsert tier" logic between the two commands if it keeps things simpler.

**Acceptance criteria**:
- `!userpermission @SomeUser 3` works the same as passing the numeric ID.
- Unit tests cover mention parsing.

**Note**: schedule after T16 — both rework the same permission commands, and the shared upsert logic is simpler against T16's tier model and escalation guard.

### T17. [x] Help / command discovery

**Problem**: There is no way to discover which commands exist or which of them the invoking user may run — a usability gap for users, moderators, and admins alike.

**Work** (implements FR-38 to FR-40):
- Add a `!help` command that lists the registered commands available to the invoking user in the current guild (required tier ≤ user's tier).
- Give every command help text: a short description and a usage/argument summary, declared on the command implementation (e.g. on `ICommand` or a parallel `HelpText` property) so it stays in sync with the code and registration.
- Support `!help <commandName>`: show the detailed help (description, usage, required permission tier) for that command — only for commands the user can run; for unavailable or unknown names reply generically, without revealing whether the command exists at a higher tier.

**Acceptance criteria**:
- `!help` output differs correctly between a tier-0 user, a moderator, and an admin on the same guild.
- `!help <command>` shows that command's description and usage; `!help` on an unknown or insufficient-tier command does not leak the command's existence details.
- Every registered command has help text; a command without it fails its unit test (or registration is rejected).
- Behavior is unit-tested.

**Note**: schedule after T16 — `!help` filters and displays the tier scale that T16 defines.

## Phase 2 — Operational base for a server-hosted bot

### T8. [x] Graceful shutdown & reconnect behavior

**Problem**: `DiscordClient.ExecuteAsync` has no handling for gateway disconnects beyond Discord.Net's built-in reconnects, and shutdown does not explicitly log out/dispose the Discord client.

**Work**:
- On cancellation (`stoppingToken`): stop/dispose the `DiscordSocketClient` cleanly and log the shutdown.
- Log gateway disconnect/reconnect events (`Disconnected`/`Reconnected`) at Warning/Information level.

**Acceptance criteria**:
- Stopping the host logs a clean Discord logout instead of dying mid-connection.
- Gateway disconnects are visible in logs.

### T9. [x] README: server run instructions

**Problem**: `README.md` currently only documents the `appsecrets.json` structure. There are no instructions for getting the bot running on a server.

**Work** — update `README.md` to include:
- Prerequisites (.NET 10 SDK for source runs; Docker for container runs; a reachable PostgreSQL instance; a Discord bot application with the **Message Content intent** enabled).
- Creating and configuring `appsecrets.json` (existing section) / environment variables for containers (depends on T3).
- Setting up the database (depends on T2: state that migrations are applied automatically on startup).
- Building & running from source: `dotnet build HambotV2.sln`, `dotnet run --project HambotV2`.
- Building & running the container: `docker build -f HambotV2/Dockerfile -t hambotv2:latest .`, `docker run` with the required environment variables/secret mounts.
- A smoke-test section (expected log lines: `Initializing Discord connection...` → gateway `Ready`).
- First-run permission setup hint (default tier 0 for everyone; raising `commandpermission`/`userpermission` tiers via the database or a first command) — include whatever behavior results from T2/T3.

**Acceptance criteria**:
- A new contributor can go from a clean clone to a running, connected bot on a server by following only the README.
- Instructions are consistent with the actual configuration keys and defaults in the code.

### T10. [x] Update the Jenkins CI pipeline

**Problem**: CI is already in place in the form of a Jenkins server, but the pipeline does not yet cover this roadmap's goals (e.g. enforcing warning-free builds); regressions (e.g. a non-lowercase command name) are only caught locally.

**Work**:
- Update the existing Jenkins pipeline so that pushes build and test the solution (`dotnet build HambotV2.sln`, `dotnet test HambotV2.sln`).
- Treat new nullable warnings as failures where feasible (without fixing the pre-existing ones yet — see T11).

**Acceptance criteria**:
- Pushes get a green/red build+test result from Jenkins automatically.
- The Jenkins job stays in sync with the solution structure (it currently needs updating whenever the build/test setup changes).

### T11. [x] Nullable-warning cleanup

**Problem**: The build emits pre-existing nullable warnings (CS8625, CS8604, CS8620, CS0162). They hide real issues (e.g. `command = null` patterns, unguarded config reads) and make `TreatWarningsAsErrors` impossible.

**Work**:
- Fix the existing warnings without changing behavior.

**Acceptance criteria**:
- `dotnet build HambotV2.sln` completes warning-free; the CI (T10) can optionally enforce it.

**Note**: schedule after T10, so CI enforcement is already in place when the build goes warning-free.

## Phase 3 — Bot features & quality of life (after the base is solid)

### T12. [x] Structured logging improvements

**Work**:
- Log command executions (user, command, result) at Information level for auditability.
- Consider Serilog with console+file sinks and a configurable log level via `appsettings.json`.

**Acceptance criteria**: command usage is traceable from logs without debugging.

### T15. [x] Command argument-parsing framework

**Problem**: Each command hand-parses `socketMessage.Content` (splitting, `TryParse`, argument-count checks) — repetitive and inconsistent.

**Work**: Introduce a small argument-parsing helper (or adopt Discord.Net's built-in command framework) so commands declare their arguments and get typed values.

**Acceptance criteria**: `CommandPermission`/`UserPermission` are refactored onto it and their tests still pass.

**Note**: schedule early in this phase, before adding more commands — the next new commands (T14's `!settings` with its subcommands, T13 stage 2's `!settings cooldown`) should be written on the framework instead of more hand-parsing.

### T13. Command cooldowns / rate limiting

**Work**: Per-user/per-command cooldown to prevent spam (e.g. a simple in-memory cache in the command pipeline). The cooldown window is resolved per permission tier: global per-tier defaults in `appsettings.json`, plus per-guild overrides configurable at runtime via `!settings cooldown` (persisted in T14's `GuildSetting` entity; staged — see the design in `software-design.md` §9.1).

**Acceptance criteria**: repeated `!hello` spam is throttled with a user-friendly response; behavior is unit-tested.

### T14. [x] Per-guild configuration and permission scoping

**Problem**: State is global to the bot; any multi-server deployment shares permission tiers and settings across guilds. Per-guild scoping is a committed requirement (FR-24 to FR-26 in [`software-requirements.md`](software-requirements.md)).

**Work**:
- Design first: add a `GuildId` dimension to `User` and `CommandMetaData`, define guild-scoped settings storage (command identifier etc., with global defaults), and settle the DM fallback behavior (FR-25).
- Document the agreed design in `software-design.md`, then implement it, including a database migration.

**Acceptance criteria**:
- The design is documented in `software-design.md` before implementation starts.
- A user's tier and a command's required tier in one guild have no effect in any other guild (FR-24).
- Guild-scoped settings override the global defaults; guilds without explicit settings fall back to them (FR-26).
- DM behavior matches the agreed fallback (FR-25).
- Behavior is covered by unit tests.

**Note**: schedule last in this phase — it is the largest change (design + migration) and builds on the tier model from T16.

## Suggested pick-up order

Completed so far: **T1**–**T4** (error handling, DB init, container secrets, config validation), **T5**–**T7**, **T16**, **T17** (parsing robustness, persistence abstraction, permission bootstrap/escalation guard, mention parsing, help command) — Phase 1 is done — and **T9** (README), **T11** (nullable-warning cleanup), **T8** (graceful shutdown/reconnect logging), **T10** (Jenkinsfile CI pipeline), **T12** (Serilog structured logging with command audit events), **T15** (the `CommandArguments` typed argument-parsing helper; all commands refactored onto it), and **T14** (per-guild scoping: composite-keyed permission state, the DM scope — owner-tier user with built-in default command tiers — `IGuildSettingsService` with per-guild command identifiers, the `!settings` command, and the `PerGuildScoping` migration with sentinel legacy rows).

Remaining work, in dependency and risk order:

1. **T13, stage 1** (in-memory throttle + global per-tier defaults from `appsettings.json`) — independent of anything else; the cooldown key is already guild-aware in its design (§9.1). Delivers the anti-spam acceptance criterion.
2. **T13, stage 2** — the per-guild runtime override: the `!settings cooldown` subcommand (written on the T15 framework), plugged into the same window-resolution chain; the `GuildSetting` cooldown columns already exist from T14's migration.

When a task is completed, tick it off here (e.g. `- [x]`) and update `software-design.md` / `software-requirements.md` so the docs keep describing the system as it actually is.
