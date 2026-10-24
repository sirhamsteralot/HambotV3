# Software Design

This document describes the current design of HambotV2 as implemented today. It is a snapshot of the existing system, intended as an overview for contributors and as a baseline for future changes.

## 1. System Overview

HambotV2 is a Discord chat bot built on .NET 10 using the generic host (`Microsoft.Extensions.Hosting`). It connects to the Discord gateway via **Discord.Net 3.20.2**, persists state in **PostgreSQL** through **EF Core 10 / Npgsql**, and processes chat commands through a small, custom command pipeline with a tiered permission system.

The solution contains two projects:

| Project | Purpose |
|---|---|
| `HambotV2/` | The bot itself (Worker SDK, generic host). Hosted service, command pipeline, EF Core model and migrations. |
| `BotTests/` | MSTest + Moq unit tests. Uses `InternalsVisibleTo` into the main project; runs without a database or Discord token. |

Key external dependencies:

- `Discord.Net` 3.20.2 — gateway connection, message events
- `Microsoft.EntityFrameworkCore` 10 / `Npgsql.EntityFrameworkCore.PostgreSQL` — persistence
- `Microsoft.Extensions.Hosting` — DI, logging, configuration, hosted services
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
                                        │ messages starting with "!"
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

### 2.1 Composition root — `HambotV2/Program.cs`

- Registers `appsecrets.json` as an additional configuration source.
- DI registrations:

| Service | Interface | Lifetime |
|---|---|---|
| `DiscordClient` | hosted service | singleton (hosted) |
| `CommandService` | `ICommandService` | singleton |
| `CommandHandler` | `ICommandHandler` | transient (resolved per message, disposed after use) |
| `PermissionsService` | `IPermissionsService` | transient |
| `DatabaseContext` | concrete `DbContext` | transient (both service and options lifetime) |

## 3. Component Design

### 3.1 Discord connection — `Connections/DiscordClient.cs`

A `BackgroundService` that:

1. Creates a `DiscordSocketClient` with `GatewayIntents.AllUnprivileged | MessageContent`.
2. Logs in with the token from configuration key `Secrets:BotToken` and starts the gateway.
3. Subscribes to `MessageReceived`. Any message whose content starts with the configured `BotConfiguration:CommandIdentifier` (default `!`) is forwarded to a freshly resolved `ICommandHandler` (resolved from `IServiceProvider`, disposed after handling).
4. Bridges `Discord.Log` events to `Microsoft.Extensions.Logging` using the configurable `BotConfiguration:LogFormat` format string.
5. Runs a keep-alive loop that logs gateway connection state every 5 minutes.

### 3.2 Command pipeline — `Commands/`

**`ICommand`** (`Commands/ICommand.cs`) — the command contract:

- `string Name` — must be lowercase (lookup lowercases input; tests enforce this).
- `Task ExecuteCommand(SocketMessage, ICommandContext)` — the command body.

**`ICommandContext` / `CommandContext`** — a thin wrapper exposing the per-message `DatabaseContext` to command implementations, keeping commands decoupled from DI.

**`CommandHandler`** (`ICommandHandler`) — per-message orchestrator, transient and disposable:

1. Resolve the command via `ICommandService.TryGetCommand`; if unknown, reply "The requested command is unavailable".
2. Check permissions via `IPermissionsService.CheckUserPermissions`; if insufficient, reply "You are not permitted to use that command".
3. Execute the command with a `CommandContext` wrapping the per-message `DatabaseContext`.

**`CommandService`** (`ICommandService`) — singleton command registry:

- Holds a `Dictionary<string, ICommand>` keyed by command name.
- Lazily populated on first lookup (`CommandsRegistered` flag) by calling `RegisterAllCommands.RegisterCommands`.
- `TryGetCommand` parses the first space-delimited token, strips the identifier character, lowercases it, and does a dictionary lookup.

**Command registration** — a small static registration chain:

- `Commands/RegisterAllCommands.cs` → delegates to `Commands/SystemCommands/RegisterSystemCommands.cs`.
- To add a command: implement `ICommand` (system commands live in `Commands/SystemCommands/`) and register it in the relevant `Register*Commands` class.

### 3.3 Permissions — `Commands/PermissionsService.cs`

Tiered integer permission model:

- A command may run when `user.PermissionTier >= commandMetaData.PermissionTier`.
- Missing `CommandMetaData` rows default to required tier 0; missing `User` rows default to tier 0.
- Tiers are read from the database per message (two lookups: command metadata + user).
- Tiers are managed at runtime via the `!commandpermission` and `!userpermission` commands (see below), so no code change or restart is needed to adjust access.

### 3.4 Persistence — `Connections/DatabaseContext.cs`

EF Core `DbContext` against PostgreSQL with two nested entity classes:

| Entity | Key | Fields |
|---|---|---|
| `User` | `DiscordId` (ulong) | `PermissionTier` (int) |
| `CommandMetaData` | `CommandName` (string) | `PermissionTier` (int) |

EF Core migrations live in `HambotV2/Migrations/` (initial create only). Regenerate with `dotnet ef migrations add <Name>` from the `HambotV2/` project directory.

There is also an `Connections/IDatabaseContext.cs` abstraction; the current `DatabaseContext` is registered and injected concretely.

### 3.5 Commands currently implemented

All in `Commands/SystemCommands/`, registered by `RegisterSystemCommands`:

| Command | Name | Behavior |
|---|---|---|
| `HelloWorld` | `!hello` | Replies "Hello World". |
| `CommandPermission` | `!commandpermission <name> [tier]` | With 1 arg: prints the command's permission tier (defaults to 0 if absent). With 2 args: sets/creates the command's tier and saves to the database. |
| `UserPermission` | `!userpermission <discordId> [tier]` | With 1 arg: prints the user's permission tier (defaults to 0 if absent). With 2 args: sets/creates the user's tier and saves to the database. |

## 4. Configuration

- `appsettings.json` (committed, non-secret): `BotConfiguration:BotName`, `BotConfiguration:LogFormat`, `BotConfiguration:CommandIdentifier` (default `!`), plus standard host logging configuration.
- `HambotV2/appsecrets.json` (gitignored): `Secrets:BotToken` (Discord bot token) and `ConnectionStrings:DefaultConnection` (PostgreSQL connection string). Loaded explicitly in `Program.cs`. Its expected structure is documented in `README.md`. Secrets must never be committed.

## 5. Deployment

The bot is containerized via `HambotV2/Dockerfile` (multi-stage .NET 10 build → `dotnet publish` → runtime-only final image). The build context is the repository root:

```bash
docker build -f HambotV2/Dockerfile -t hambotv2:latest .
docker run -d --name hambot-smoke hambotv2:latest
```

- The container performs a real Discord login using `appsecrets.json`; the database is only touched when a command executes. The default connection string assumes Postgres at `host.docker.internal:5432`.
- Caution: files under `HambotV2/` matching SDK publish rules (including the gitignored `appsecrets.json`) are copied into the image. Do not push the image to a shared registry without checking what secrets are baked in.

## 6. Testing

- `BotTests/` contains MSTest + Moq tests: `TestCommandInfrastructure.cs`, `TestCommands.cs`, `TestPermissions.cs`.
- Tests run without a database or Discord token (in-memory configuration, Moq for dependencies).
- Coverage focuses on: command names being lowercase, system command registration, command lookup, and permission tier logic.
- Build and run:

  ```bash
  dotnet build HambotV2.sln
  dotnet test HambotV2.sln
  ```

- The build currently emits several pre-existing nullable warnings (CS8625, CS8604, CS8620, CS0162); these are not treated as failures, but new code should not add new ones.

## 7. Conventions

- C# with nullable reference types and implicit usings enabled; `LangVersion` latest in the test project.
- Bot-internal types are `internal` (tests rely on `InternalsVisibleTo`, which also grants `DynamicProxyGenAssembly2` for Moq); new command implementations should be `internal` too.
- Command names must be lowercase.
- DI lifetimes: `CommandService` singleton; `CommandHandler`, `PermissionsService`, `DatabaseContext` transient; handler resolved per message and disposed after use.

## 8. Known Design Considerations / Current Limitations

These are observations of the current implementation, not roadmap commitments:

- **Error handling**: exceptions thrown inside a command or the handler are not caught explicitly at the message-processing level; a failing command surfaces as an unhandled Discord event handler exception rather than a user-facing error.
- **Database initialization**: migrations exist but are not applied automatically at startup; the database must be prepared out-of-band.
- **Command parsing**: `TryGetCommand` assumes the first token is at least one character long after the identifier; the identifier check is a simple `StartsWith` on raw content.
- **Permission commands take raw IDs**: `!userpermission` expects a numeric Discord ID argument rather than a mention.
- **Secrets in image**: the Docker publish step currently copies `appsecrets.json` into the image (see §5).
- **`IDatabaseContext`** exists but is unused; injection is against the concrete `DatabaseContext`.
