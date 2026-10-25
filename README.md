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
- **A Discord bot application** — create one at <https://discord.com/developers/applications>, note the bot token, and enable the **Message Content intent** (Bot → Privileged Gateway Intents), otherwise commands will not be received. Then invite the bot to your server with the link below.

## Inviting the bot

Generate the invite link in the developer portal (OAuth2 → URL Generator) with **scopes** `bot` + `applications.commands` and the bot permissions below, or use the ready-made link (Application ID already filled in):

```
https://discord.com/oauth2/authorize?client_id=<bot id>&scope=bot+applications.commands&permissions=8533303368350784
```

The permissions integer (`8533303368350784`) covers everything the bot needs and reasonable extras (View Channels, Send Messages, Embed Links, Read Message History, Attach Files, Manage Messages, thread and voice-message support, etc.). The `applications.commands` scope reserves support for slash commands later; it grants no extra permissions and does nothing until the bot registers commands.

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

## Backups

The Compose stack includes a dedicated `backup` sidecar (built from `scripts/backup/`), so the database is backed up on schedule even while the bot container is stopped, restarting, or failing. Backups are configured entirely through `.env` (see `.env.example`):

| Key | Default | Meaning |
|---|---|---|
| `BACKUP_INTERVAL` | `86400` (24 h) | Seconds between scheduled backups. |
| `BACKUP_RETENTION` | `7` | How many of the most recent dumps to keep in the `backups` volume. |
| `BACKUP_TEST_INTERVAL` | `2592000` (30 days) | Seconds between automated restore tests. |
| `BACKUP_RCLONE_REMOTE` | *(empty)* | Offsite push target, e.g. `backup:my-bucket/hambotv2` (S3-compatible storage via rclone). Leave empty to skip the offsite push. |
| `BACKUP_S3_*` | *(empty)* | Offsite storage credentials (endpoint, region, access key, secret) — only used when `BACKUP_RCLONE_REMOTE` is set. |

Each pass of the backup loop: `pg_dump -Fc` into the shared `backups` volume → validate the dump (`pg_restore --list` plus a size floor — an invalid dump is deleted and counts for nothing) → prune local dumps beyond the retention count (only after a good dump, so a failed dump never deletes good backups) → push offsite when configured (a failed push is logged and otherwise harmless) → write the `backups/.backup-status` file that `!health` and the watchdog read. On `BACKUP_TEST_INTERVAL` (default monthly, and immediately on a fresh deployment) the loop also proves restorability: it restores the newest valid dump into a scratch database, verifies the expected tables, drops the scratch database again, and records the outcome.

### Restoring a backup — two paths

**From Discord (healthy bot)** — bot-owner tier, works in DMs:

- `!backup` — creates an on-demand backup immediately (same format and directory as the scheduled ones) and replies with its name and size.
- `!backups` — lists the available backups, newest first (capped at 10 entries).
- `!restorebackup <name>` — warns that all current data will be replaced and asks you to confirm.
- `!restorebackup <name> confirm` — performs the restore. The bot enters maintenance mode while it runs (all other commands decline), pooled database connections are cleared, and the guild-settings cache is invalidated afterwards.

**From the host (broken bot or broken database)** — `scripts/restore-backup.sh <dump-file>` is the authoritative recovery path; it runs from outside the stack: stop the bot → drop and recreate the database → `pg_restore` the dump → restart → verify the known startup lines in the logs. See the header of the script for the expected environment.

## Watchdog, health & operator notifications

- `!health` — bot-owner tier, works in DMs: one report with the bot's version, uptime, gateway state, database reachability, pending reminder count, backup and restore-test status, and the configured notification channel. (LLM, MCP, and presence fields join the report when those features land.)
- `!notifychannel` — bot-owner tier, works in DMs: show / set / clear the operator notification channel (`!notifychannel <#channel>` or a raw channel id; `!notifychannel clear`). The setting persists across restarts; `NOTIFY_CHANNEL_ID` in `.env` is the configuration default.
- When a notification channel is set, command execution failures, retry-exhausted reminder deliveries, maintenance-mode entry/exit and restore outcomes, and watchdog alerts are posted there — rate-limited (default 5 posts per minute) with identical consecutive messages collapsed, so an error storm cannot flood the channel. Failed notification posts are logged and additionally DM'd to the bot owner.
- The watchdog checks every 5 minutes (`WATCHDOG_CHECK_INTERVAL_MINUTES`) and alerts the bot owner by DM (and the notification channel) when: the gateway has been down for more than 10 minutes (`WATCHDOG_GATEWAY_DOWN_ALERT_MINUTES`), a backup failed validation or is overdue, a restore test failed or is overdue, the bot is in maintenance mode, or the notification channel is unreachable. Each condition alerts once, re-alerts hourly (`WATCHDOG_RE_ALERT_MINUTES`) while it persists, and announces recovery once. Without a configured `BOT_OWNER_DISCORD_ID`, alerts degrade to logs.

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

They cover command registration (lowercase names, help text, default tiers), command lookup and parsing edge cases, the typed argument-parsing helper, the permission tier scale with tier overrides and escalation guards, per-guild scoping and the DM scope, the permission-management, `!settings` and `!help` command behavior, command cooldowns, startup configuration validation, the command pipeline failure paths (throwing commands, database errors during permission checks), the backup commands (trigger, list, two-step restore confirmation, maintenance-mode gating), and the watchdog/health/notification surface (condition detection, alert cadence, `!health`, `!notifychannel`).

## Running in production

**Docker Compose (recommended)** — same as the quick start, on your server:

```bash
cp .env.example .env   # fill in your real token and a strong database password
docker compose up -d --build
```

- Database data persists in the `db-data` volume across restarts; backups persist in the `backups` volume.
- All services use `restart: unless-stopped`, so the stack survives crashes and (with Docker running) host reboots.
- The bot container waits for the database healthcheck before starting; the backup sidecar waits for it too.
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
| `!health` | Shows the bot's operational state: version, uptime, gateway, database, pending reminders, backups, restore test, notification channel. Bot-owner tier; works in DMs. |
| `!notifychannel [<#channel or id>` / `clear`]` | Shows, sets, or clears the operator notification channel where errors and alerts are posted. Bot-owner tier; works in DMs. |
| `!backup` | Creates a database backup immediately and replies with its name and size. Bot-owner tier; works in DMs. |
| `!backups` | Lists the available backups, newest first. Bot-owner tier; works in DMs. |
| `!restorebackup <name> [confirm]` | Restores a backup — destructive, so it demands the explicit `confirm` step. Bot-owner tier; works in DMs. |

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

