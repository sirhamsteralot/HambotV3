# HambotV2

A Discord chat bot built on .NET 10 with [Discord.Net](https://github.com/DiscordNet/Discord.Net), EF Core 10 / Npgsql (PostgreSQL), and the .NET generic host.

## Quick start with Docker Compose (recommended)

The fastest way to a running bot — Compose brings up PostgreSQL and the bot together, with no manual database setup:

```bash
cp .env.example .env   # then fill in DISCORD_BOT_TOKEN and POSTGRES_PASSWORD
docker compose up -d --build
docker compose logs -f bot
```

That's it: the database schema is created automatically on startup (EF Core migrations are applied by the bot), and the bot connects to Discord. Stop everything with `docker compose down` (add `-v` to also delete the database data).

## Prerequisites

- **Docker** (with the Compose plugin) — for the quick start and production container runs.
- **.NET 10 SDK** (`10.0.x`) — only for building/running from source or running the tests.
- **A Discord bot application** — create one at <https://discord.com/developers/applications>, note the bot token, and enable the **Message Content intent** (Bot → Privileged Gateway Intents), otherwise commands will not be received. Invite the bot to your server (OAuth2 → URL Generator → `bot` scope).

## Configuration

Non-secret settings live in `appsettings.json` (committed):

| Key | Default | Meaning |
|---|---|---|
| `BotConfiguration:BotName` | `HamBot` | Bot display name. |
| `BotConfiguration:LogFormat` | `{0}: {1}` | Format string used to bridge Discord.Net log events. |
| `BotConfiguration:CommandIdentifier` | `!` | Prefix that marks a message as a command. |

Secrets are provided differently depending on how you run the bot:

| Key | Where |
|---|---|
| `Secrets:BotToken` | Discord bot token. |
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string. |

- **Docker Compose** — set `DISCORD_BOT_TOKEN` and `POSTGRES_PASSWORD` in `.env` (see `.env.example`); Compose passes them into the containers as the environment variables `Secrets__BotToken` and `ConnectionStrings__DefaultConnection`. Environment variables take precedence over any file-based configuration.
- **From source / plain Docker** — put both keys in `HambotV2/appsecrets.json` (gitignored), loaded automatically at startup:

  ```json
  {
    "Secrets": {
      "BotToken": "PlaceholderToken"
    },
    "ConnectionStrings": {
      "DefaultConnection": "Server=localhost:5432;Database=hambotdb;User Id=hambot;Password=PlaceholderPassword;"
    }
  }
  ```

  Use `localhost` in `Server=` when running from source; use `host.docker.internal` when running the bot in a plain container against Postgres on the same host.

All required keys are validated at startup: if any are missing, the bot logs a critical message naming them and exits with a non-zero code instead of failing later with unclear errors.

Never commit secrets: `.env`, and `appsecrets.json` are gitignored. Keep it that way.

## Database

No manual migration step is required: on startup the bot applies the EF Core migrations from `HambotV2/Migrations/` automatically (`Database.Migrate()` in `Program.cs`). An empty database is enough — the Compose setup creates one from scratch.

If the database is unreachable at startup, the bot logs a critical error and exits with a non-zero code instead of starting half-functional — so a failed startup is always visible.

## Building & running locally (from source)

You need a reachable PostgreSQL instance; the easiest option is a container:

```bash
docker run -d --name hambot-postgres \
  -e POSTGRES_USER=hambot -e POSTGRES_PASSWORD=PlaceholderPassword \
  -e POSTGRES_DB=hambotdb -p 5432:5432 postgres:17
```

Then configure `HambotV2/appsecrets.json` (connection string pointing at `localhost:5432`) and run:

```bash
dotnet build HambotV2.sln
dotnet run --project HambotV2
```

Stop the bot with `Ctrl+C`.

## Running the tests

The unit tests (MSTest + Moq, in `BotTests/`) run without a database, Discord token, or Docker:

```bash
dotnet test HambotV2.sln
```

They cover command registration, command lookup, permission tier logic, startup configuration validation, and the command pipeline failure paths (throwing commands, database errors during permission checks).

## Running in production

**Docker Compose (recommended)** — same as the quick start, on your server:

```bash
cp .env.example .env   # fill in your real token and a strong database password
docker compose up -d --build
```

- Database data persists in the `db-data` volume across restarts.
- Both services use `restart: unless-stopped`, so the stack survives crashes and (with Docker running) host reboots.
- The bot container waits for the database healthcheck before starting.
- Check logs with `docker compose logs -f bot`, and update to a new build with `docker compose up -d --build`.

**Plain Docker** (no Compose) — build from the repository root (the Dockerfile's build context is the repo root):

```bash
docker build -f HambotV2/Dockerfile -t hambotv2:latest .
docker run -d --name hambot --restart unless-stopped \
  -e Secrets__BotToken=<your-token> \
  -e "ConnectionStrings__DefaultConnection=Server=<your-postgres-host>;Port=5432;Database=hambotdb;User Id=hambot;Password=<password>;" \
  hambotv2:latest
```

The image is secret-free: `appsecrets.json` is excluded from the build context (`.dockerignore`), so it can be pushed to a registry — secrets always come in via environment variables.

## Smoke test

Expected startup log sequence (from source, Compose, or plain Docker):

1. `Database migrations applied successfully`
2. `Initializing Discord connection...`
3. Gateway ready (Discord.Net logs a `Ready` message; afterwards `Worker running at: ...` lines appear every 5 minutes).

Then, in a Discord channel the bot can see, send `!hello` — it should reply `Hello World`.

If the token is wrong/missing you'll instead see a critical message naming the missing keys, or `Discord: A supplied token was invalid.` — the bot keeps retrying, it does not crash.

## Commands & first-run permissions

| Command | Behavior |
|---|---|
| `!hello` | Replies "Hello World". |
| `!commandpermission <name> [tier]` | Prints a command's required tier, or sets/creates it. |
| `!userpermission <discordId> [tier]` | Prints a user's tier, or sets/creates it. |

Users and commands default to permission tier 0, so every command is usable by everyone until tiers are raised (e.g. `!commandpermission userpermission 1`). Note that this means permission management is effectively **open on a fresh guild** until someone locks it down — a bootstrap admin/role model is planned as roadmap task T16.
