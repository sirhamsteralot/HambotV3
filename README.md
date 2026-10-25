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
| `BotConfiguration:CooldownSecondsByTier` | `{ "0": 3, "1": 3, "2": 2, "3": 1 }` | Command cooldown window in seconds per permission tier (missing entries default to 3; `0` disables throttling for that tier). Servers override these at runtime via `!settings cooldown`. |
| `Serilog` section | Console + daily `logs/` file sink | Logging configuration: minimum level (with per-namespace overrides) and sinks. Change the log level without rebuilding; log files are written to `logs/` (gitignored). |

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

They cover command registration (lowercase names, help text, default tiers), command lookup and parsing edge cases, the typed argument-parsing helper, the permission tier scale with tier overrides and escalation guards, per-guild scoping and the DM scope, the permission-management, `!settings` and `!help` command behavior, command cooldowns, startup configuration validation, and the command pipeline failure paths (throwing commands, database errors during permission checks).

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

## Continuous integration & deployment

The repository ships a Jenkins pipeline ([`Jenkinsfile`](Jenkinsfile)): every build compiles the solution with warnings-as-errors and runs the unit tests, and — for version tags (`v<major.minor.patch>`) only — deploys the exact tested commit to the production server over SSH (build image, verify startup, auto-rollback on failure). See **[docs/jenkins-cicd.md](docs/jenkins-cicd.md)** for the full setup guide.

## Smoke test

Expected startup log sequence (from source, Compose, or plain Docker):

1. `Bot version: <version>` — the release tag semver plus the build commit's short hash (e.g. `1.2.3+1639324`; fallback `0.0.0+<hash>` on untagged commits)
2. `Database migrations applied successfully`
3. `Initializing Discord connection...`
4. Gateway ready (Discord.Net logs a `Ready` message; afterwards `Worker running at: ...` lines appear every 5 minutes).

Then, in a Discord channel the bot can see, send `!hello` — it should reply `Hello World`.

If the token is wrong/missing you'll instead see a critical message naming the missing keys, or `Discord: A supplied token was invalid.` — the bot keeps retrying, it does not crash.

## Commands & first-run permissions

| Command | Behavior |
|---|---|
| `!hello` | Replies "Hello World". |
| `!version` | Replies with the bot's version: the release tag semver plus the build commit's short hash (e.g. `1.2.3+1639324`). Works in DMs. |
| `!help [commandName]` | Lists the commands you can use, or shows detailed help (description, usage, required tier) for one command. |
| `!commandpermission <name> [tier]` | Prints a command's required tier, or sets/creates it. Admin-only by default. |
| `!userpermission <user> [tier]` | Prints a user's tier, or sets/creates it. `<user>` is a raw Discord id or an @mention. Admin-only by default. |
| `!settings` | Shows the server's effective command identifier and per-tier cooldown overrides. Admin-only by default. |
| `!settings commandidentifier <value>` / `reset` | Sets or clears the server's command identifier override (≤ 5 chars; `reset` reverts to the global default). |
| `!settings cooldown <tier> <seconds>` / `reset` | Sets or clears the server's per-tier cooldown override (tiers 0–3; `0` disables throttling for that tier). |

Permission tiers form a named scale:

| Tier | Name | Meaning |
|---|---|---|
| 0 | everyone | Default for users and most commands. |
| 1 | moderator | Trusted helpers. |
| 2 | admin | Can manage permissions (below their own tier, see below). |
| 3 | owner | Full control of a guild; held automatically by the guild owner. |
| 4 | bot owner | Highest tier of all, above every guild. Configured per bot, not per guild. |

On a fresh guild (empty database) **nobody can grant themselves permissions**: the permission-management commands require admin tier by default, and their escalation guard only allows assigning tiers strictly below your own — you can never modify your own tier or that of an equal/higher-tiered user.

Bootstrap administrator: the **guild owner** automatically acts at owner tier in their guild, without any database entry. That is how the first admin exists on a fresh guild; they can then grant tiers to others with `!userpermission`.

The **bot owner** is configured in `.env` as `BOT_OWNER_DISCORD_ID` (wired through to `Permissions:BotOwnerDiscordId`). They hold tier 4 in every guild — the highest permission level of all, above guild owners — and are the designated operator for things that apply across guilds (e.g. managing any guild's settings by running `!settings` there). The tier comes from configuration only: it can never be granted through commands, not even by the bot owner themselves.

Permission tiers and bot settings are **scoped per guild**: a user's tier or a command's required tier in one server has no effect in any other, and each server can override the global command identifier with `!settings commandidentifier <value>` (admins; `!settings commandidentifier reset` reverts to the global default). In **direct messages**, commands run in a DM scope: you act at owner tier and commands require their built-in default tiers — no per-server state is read or written. Commands that manage per-server state (`!userpermission`, `!commandpermission`, `!settings`) are only available in servers.

Tiers are then managed at runtime, e.g.:

- `!userpermission @Someone 1` — grants moderator tier (as owner/admin).
- `!commandpermission hello 1` — restricts `!hello` to moderators and above.

