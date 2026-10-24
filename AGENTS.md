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

- Tests run without a database or Discord token; they use Moq and in-memory configuration. All tests are in `BotTests/` (`TestCommandInfrastructure.cs`, `TestCommands.cs`, `TestPermissions.cs`).
- The build currently emits several pre-existing nullable warnings (CS8625, CS8604, CS8620, CS0162) — don't treat them as new failures, but avoid adding new ones.

## Docker

The bot is containerized via `HambotV2/Dockerfile` (multi-stage .NET 10 build → `dotnet publish` → runtime-only final image). **The build context is the repository root**, not the project directory:

```bash
# build (from repo root)
docker build -f HambotV2/Dockerfile -t hambotv2:latest .

# smoke test: container should log "Initializing Discord connection..." and reach "Gateway: Ready"
docker run -d --name hambot-smoke hambotv2:latest
docker logs -f hambot-smoke

# inspect the image contents if needed
docker run --rm --entrypoint ls hambotv2:latest -la /app
```

- Running the container performs a real Discord login using `appsecrets.json`; it does not touch the database until a command executes (Postgres at `host.docker.internal:5432` per the connection string).
- Note: files under `HambotV2/` matching the SDK's publish rules (including the gitignored `appsecrets.json`) are currently copied into the image. Do not push this image to a shared registry without checking what secrets are baked in.

## Architecture

Entry point is `HambotV2/Program.cs`: a generic host registers `DatabaseContext` (transient Npgsql), a `DiscordClient` hosted service, and the command pipeline.

### Message flow

1. `Connections/DiscordClient.cs` (BackgroundService) connects to Discord with the token from `Secrets:BotToken`. Messages starting with the configured `BotConfiguration:CommandIdentifier` (default `!`) are forwarded to `ICommandHandler`.
2. `Commands/CommandHandler.cs` resolves the command via `ICommandService.TryGetCommand` (the command name is the first space-delimited token minus the identifier, lowercased), checks permissions via `IPermissionsService`, then executes the command with a `CommandContext` wrapping the `DatabaseContext`.
3. Commands implement `Commands/ICommand.cs` (`Name` + `ExecuteCommand(SocketMessage, ICommandContext)`).

### Command registration

- `Commands/CommandService.cs` is a singleton holding a `Dictionary<string, ICommand>`, lazily populated on first use via `Commands/RegisterAllCommands.cs`.
- To add a command: create a class implementing `ICommand` in an appropriate namespace (system commands live in `Commands/SystemCommands/`), then register it in the relevant `Register*Commands` class (e.g. `SystemCommands/RegisterSystemCommands.cs`).
- **Command names must be lowercase** — lookup lowercases input, and tests assert `Name == Name.ToLower()`.

### Permissions

Tiered integer permission system (`Commands/PermissionsService.cs`): a user may run a command when their `User.PermissionTier` >= the command's `CommandMetaData.PermissionTier`. Missing DB entries default to tier 0. Tiers are managed at runtime via the `!commandpermission` and `!userpermission` commands.

### Data model

`Connections/DatabaseContext.cs` defines two DbSets with nested entity classes:

- `User` (`DiscordId` key, `PermissionTier`)
- `CommandMetaData` (`CommandName` key, `PermissionTier`)

EF Core migrations live in `HambotV2/Migrations/`. Regenerate with `dotnet ef migrations add <Name>` from the `HambotV2/` project directory (connection string comes from `appsecrets.json`, see below).

## Configuration & Secrets

- `appsettings.json` holds non-secret bot config (`BotConfiguration:BotName`, `LogFormat`, `CommandIdentifier`).
- **`HambotV2/appsecrets.json` is gitignored** and holds the Discord bot token (`Secrets:BotToken`) and the PostgreSQL connection string (`ConnectionStrings:DefaultConnection`). Its expected structure is documented in `README.md`. Never commit secrets; never reference real tokens in code or tests.

## Conventions

- C# with nullable reference types and implicit usings enabled; `LangVersion` is `latest` in the test project.
- Most bot-internal types are `internal` (tests rely on `InternalsVisibleTo`); keep new command implementations `internal` too.
- DI lifetimes: `CommandService` is a singleton; `CommandHandler`, `PermissionsService`, and `DatabaseContext` are transient. The handler is resolved per message and disposed after use.
- Container deployment via `HambotV2/Dockerfile` (targets the `HambotV2` project; connection string defaults assume `host.docker.internal:5432`).
