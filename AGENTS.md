# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project Overview

HambotV2 is a Discord chat bot built on .NET 10 (`net10.0`) using:

- **Discord.Net 3.20.2** for the Discord gateway connection
- **EF Core 10 / Npgsql** against a PostgreSQL database
- **Microsoft.Extensions.Hosting** (Worker service / generic host)
- **MSTest + Moq** for unit tests in the `BotTests` project

The solution (`HambotV2.sln`) contains two projects:

- `HambotV2/` — the bot itself (Worker SDK)
- `BotTests/` — unit tests (has `InternalsVisibleTo` into the main project)

## Build & Test

```bash
dotnet build HambotV2.sln
dotnet test HambotV2.sln
```

- Building requires the .NET 10 SDK (`10.0.x`).

- Tests run without a database or Discord token; they use Moq and in-memory configuration. All tests are in `BotTests/` (`TestCommandInfrastructure.cs`, `TestCommands.cs`, `TestPermissions.cs`, `TestCommandHandler.cs`, `TestConfigurationValidator.cs`, `TestPermissionCommands.cs`, `TestHelp.cs`, `TestCommandArguments.cs`, `TestGuildSettings.cs`, `TestCooldowns.cs`, plus the shared `DiscordTestDoubles.cs`).
- The build is warning-free; keep it that way and avoid introducing new warnings.

## Docker

The bot is containerized via `HambotV2/Dockerfile` (multi-stage .NET 10 build → `dotnet publish` → runtime-only final image). **The build context is the repository root**, not the project directory. The recommended way to run everything (bot + PostgreSQL) is `docker-compose.yml` at the repo root:

```bash
# configure secrets (see .env.example)
cp .env.example .env   # fill in DISCORD_BOT_TOKEN and POSTGRES_PASSWORD

# build & run bot + database
docker compose up -d --build
docker compose logs -f bot   # should log "Database migrations applied successfully", then "Initializing Discord connection..."
```

Plain Docker works too, with secrets passed as environment variables (`Secrets__BotToken`, `ConnectionStrings__DefaultConnection`):

```bash
# build (from repo root)
docker build -f HambotV2/Dockerfile -t hambotv2:latest .

# smoke test
docker run -d --name hambot-smoke -e Secrets__BotToken=... -e "ConnectionStrings__DefaultConnection=..." hambotv2:latest
docker logs -f hambot-smoke
```

- Startup applies EF Core migrations automatically and validates required configuration; missing keys or an unreachable database exit with a non-zero code and a critical log line.
- The image is secret-free: `appsecrets.json` and `.env` are excluded from the build context via `.dockerignore` (local dev file loading remains a fallback for source runs).

## Architecture

Entry point is `HambotV2/Program.cs`: a generic host registers `DatabaseContext` (transient Npgsql), a `DiscordClient` hosted service, and the command pipeline.

### Message flow

1. `Connections/DiscordClient.cs` (BackgroundService) connects to Discord with the token from `Secrets:BotToken`. Messages starting with the configured `BotConfiguration:CommandIdentifier` (default `!`) are forwarded to `ICommandHandler`.
   - **Gateway intents**: request only what the bot uses — currently `Guilds | GuildMessages | DirectMessages | MessageContent` in `DiscordClient.cs`. Do NOT switch back to `GatewayIntents.AllUnprivileged`: it triggers Discord.Net "using intent without listening to any events" warnings on every connect. When adding a feature that needs a new intent (e.g. reactions, members), add it explicitly and keep this list current.
2. `Commands/CommandHandler.cs` resolves the command via `ICommandService.TryGetCommand` (the command name is the first space-delimited token minus the identifier, lowercased), checks permissions via `IPermissionsService`, then executes the command with a `CommandContext` wrapping the `DatabaseContext`.
3. Commands implement `Commands/ICommand.cs` (`Name`, `Description`, `Usage`, `DefaultPermissionTier` + `ExecuteCommand(IMessage, ICommandContext)`).

### Command registration

- `Commands/CommandService.cs` is a singleton holding a `Dictionary<string, ICommand>`, lazily populated on first use via `Commands/RegisterAllCommands.cs`.
- To add a command: create a class implementing `ICommand` in an appropriate namespace (system commands live in `Commands/SystemCommands/`), then register it in the relevant `Register*Commands` class (e.g. `SystemCommands/RegisterSystemCommands.cs`).
- **Command names must be lowercase** — lookup lowercases input, and tests assert `Name == Name.ToLower()`.
- **Every command must provide help text** — implement `Description` (one-line summary shown by `!help`) and `Usage` (argument summary shown by `!help <name>`) on the command implementation. Unit tests enforce non-empty values for all registered commands. See `docs/software-design.md` §3.2 for the help-command design.

### Permissions

Tiered integer permission system (`Commands/PermissionsService.cs`): a user may run a command when their `User.PermissionTier` >= the command's `CommandMetaData.PermissionTier`. Missing DB entries default to tier 0. Tiers are managed at runtime via the `!commandpermission` and `!userpermission` commands.

### Data model

`Connections/DatabaseContext.cs` defines three DbSets with nested entity classes, all scoped per guild:

- `User` (composite key `GuildId` + `DiscordId`, `PermissionTier`)
- `CommandMetaData` (composite key `GuildId` + `CommandName`, `PermissionTier`)
- `GuildSetting` (key `GuildId`; `CommandIdentifier` override, per-tier cooldown overrides used by the cooldown service)

EF Core migrations live in `HambotV2/Migrations/`. Regenerate with `dotnet ef migrations add <Name>` from the `HambotV2/` project directory (a `DesignTimeDatabaseContextFactory` lets the tools scaffold without running startup; connection string comes from `appsecrets.json`, see below).

## Configuration & Secrets

- `appsettings.json` holds non-secret bot config (`BotConfiguration:BotName`, `LogFormat`, `CommandIdentifier`).
- **`HambotV2/appsecrets.json` is gitignored** and holds the Discord bot token (`Secrets:BotToken`) and the PostgreSQL connection string (`ConnectionStrings:DefaultConnection`). Its expected structure is documented in `README.md`. Never commit secrets; never reference real tokens in code or tests.

## Git

- **Do not stage or commit anything to git yourself.** Never run `git add` or `git commit`; leave all changes unstaged in the working tree and let the user handle version control.

## Conventions

- C# with nullable reference types and implicit usings enabled; `LangVersion` is `latest` in the test project.
- Most bot-internal types are `internal` (tests rely on `InternalsVisibleTo`); keep new command implementations `internal` too.
- DI lifetimes: `CommandService` is a singleton; `CommandHandler`, `PermissionsService`, and `DatabaseContext` are transient. The handler is resolved per message and disposed after use.
- Container deployment via `HambotV2/Dockerfile` (targets the `HambotV2` project) or `docker-compose.yml` (bot + PostgreSQL; connection string defaults to the compose `db` service, plain containers use `host.docker.internal:5432`).
