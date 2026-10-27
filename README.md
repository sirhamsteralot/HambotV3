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

The permissions integer (`8533303368350784`) covers everything the bot needs and reasonable extras (View Channels, Send Messages, Embed Links, Read Message History, Attach Files, Manage Messages, thread and voice-message support, etc.). The `applications.commands` scope enables the opt-in slash-command surface (`SLASH_COMMANDS_ENABLED`); it grants no extra permissions and does nothing until the bot registers commands.

## Configuration

Non-secret settings live in `appsettings.json` (committed):

| Key | Default | Meaning |
|---|---|---|
| `BotConfiguration:BotName` | `HamBot` | Bot display name. |
| `BotConfiguration:LogFormat` | `{0}: {1}` | Format string used to bridge Discord.Net log events. |
| `BotConfiguration:CommandIdentifier` | `!` | Prefix that marks a message as a command. |
| `BotConfiguration:CooldownSecondsByTier` | `{ "0": 3, "1": 3, "2": 2, "3": 1 }` | Command cooldown window in seconds per permission tier (missing entries default to 3; `0` disables throttling for that tier). Servers override these at runtime via `!settings cooldown`. |
| `BotConfiguration:Presence:Type` / `BotConfiguration:Presence:Text` | *(unset)* | Optional startup activity: `playing`, `watching`, `listening`, or `competing` plus its text (single line, ≤ 128 chars), applied once the gateway is ready. Invalid values fail startup naming the key; runtime changes via `!presence` are not persisted. |
| `BotConfiguration:Bio` | *(unset)* | Optional startup profile "About Me" (≤ 190 chars, line breaks allowed), applied in every guild the bot is a member of. Absent → an existing bio is left untouched; runtime changes via `!bio` are not persisted. |
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
    },
    "Mcp": {
      "Enabled": false,
      "ApiKey": "OptionalUnlessEnabled",
      "ListenUri": "http://0.0.0.0:2525"
    }
  }
  ```

  The `Mcp` section is optional (the endpoint is disabled without it); `ApiKey` is required when `Enabled` is true.

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

- `!health` — bot-owner tier, works in DMs: one report with the bot's version, uptime, gateway state, database reachability, pending reminder count, backup and restore-test status, the configured notification channel, and the MCP endpoint state. (The LLM field joins the report when that feature lands; presence and bio are deliberately not part of it — `!presence` and `!bio` own that surface.)
- `!notifychannel` — bot-owner tier, works in DMs: show / set / clear the operator notification channel (`!notifychannel <#channel>` or a raw channel id; `!notifychannel clear`). The setting persists across restarts; `NOTIFY_CHANNEL_ID` in `.env` is the configuration default.
- When a notification channel is set, command execution failures, retry-exhausted reminder deliveries, maintenance-mode entry/exit and restore outcomes, and watchdog alerts are posted there — rate-limited (default 5 posts per minute) with identical consecutive messages collapsed, so an error storm cannot flood the channel. Failed notification posts are logged and additionally DM'd to the bot owner.
- The watchdog checks every 5 minutes (`WATCHDOG_CHECK_INTERVAL_MINUTES`) and alerts the bot owner by DM (and the notification channel) when: the gateway has been down for more than 10 minutes (`WATCHDOG_GATEWAY_DOWN_ALERT_MINUTES`), a backup failed validation or is overdue, a restore test failed or is overdue, the bot is in maintenance mode, or the notification channel is unreachable. Each condition alerts once, re-alerts hourly (`WATCHDOG_RE_ALERT_MINUTES`) while it persists, and announces recovery once. Without a configured `BOT_OWNER_DISCORD_ID`, alerts degrade to logs.

## MCP endpoint (agent API)

Opt-in, agent-facing API for the operator's tooling (design §3.11): a [Model Context Protocol](https://modelcontextprotocol.io) server over streamable HTTP, hosted in-process by the bot and authenticated with a bearer key. Every request must carry `Authorization: Bearer <MCP_API_KEY>`; anything else is rejected with 401 before any tool runs.

- **Configuration**: `MCP_ENABLED` / `MCP_API_KEY` / `MCP_LISTEN_URI` in `.env` (from source: the `Mcp` section of `appsecrets.json`). Default: disabled, listening at `http://0.0.0.0:2525` when enabled; the compose file publishes that port **loopback-only** (`127.0.0.1:2525:2525`), so an agent on the deployment host reaches `http://127.0.0.1:2525/mcp` while the internet cannot. Enabled without a key → the endpoint stays down.
- **API key**: the key is just a long random secret you invent once — treat it like the bot token, and never commit it. Generate one, e.g.:

  ```bash
  openssl rand -hex 32
  ```

  Put it in `.env` as `MCP_API_KEY` (or `"ApiKey"` in the `Mcp` section of `appsecrets.json`), set `MCP_ENABLED=true`, restart, and confirm the startup log line `MCP endpoint listening at http://0.0.0.0:2525/mcp`. Rotating the key is the same procedure with a new value; a restart always applies the configured state.
- **Connecting**: any MCP-capable client works — point it at the endpoint URL and pass the key as the bearer header:

  ```json
  {
    "mcpServers": {
      "hambot": {
        "url": "http://127.0.0.1:2525/mcp",
        "headers": { "Authorization": "Bearer <your-key>" }
      }
    }
  }
  ```

  The port is loopback-only in Compose, so a client on another machine needs a tunnel to the deployment host first (e.g. `ssh -L 2525:127.0.0.1:2525 <host>`).
- **Tools**: `bot_status` (version, uptime, gateway state, guild count, presence, bio), `list_guilds`, `list_commands` (every command's name, tier, description, and usage — the discoverable surface for `run_command`), `set_presence` and `set_bio` (the same limits and services as `!presence`/`!bio`), `get_recent_messages(channelId, limit)` (≤ 50), `send_message(channelId, text)` (plain text ≤ 2000 chars), and `run_command(guildId, commandLine)` — a virtual invocation through the real command pipeline as the operator's agent: same permissions, cooldowns, custom responses, and audit events as a chat invocation, with the command's reply captured and returned as the tool result, never posted to a channel.
- **Per-server authority**: the agent acts at the lowest tier (0) by default — it can only run commands everyone can. A server owner or the bot owner raises it per server with `!agenttier <tier 0-4>` (`!agenttier` shows, `!agenttier clear` resets to the default). Commands whose required tier exceeds it are denied like any other invocation, and the agent itself can never change its own ceiling.

## Slash commands

Opt-in via `SLASH_COMMANDS_ENABLED=true` (default false), exposed when the bot was invited with the `applications.commands` scope: the administrative/configuration commands (`/settings`, `/backup`, `/userpermission`, `/banuser`, ...), the personal reminder commands (`/remindme`, `/reminders`, `/cancelreminder`), and `/help` are available as Discord application commands. They run through the same command pipeline — identical permission tiers, cooldowns, and reply text — but every reply is **ephemeral** (visible only to the invoker); a multi-page `/help` list carries previous/next buttons that page it in place. The community-facing commands (`!quote` & co., the custom responses, `!version`) stay text-only, and text commands keep working either way. Global commands can take up to an hour to appear after the first registration.

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

They cover command registration (lowercase names, help text, default tiers), command lookup and parsing edge cases, the typed argument-parsing helper, the permission tier scale with tier overrides and escalation guards, per-guild scoping and the DM scope, the permission-management, `!settings`, `!agenttier` and `!help` command behavior, command cooldowns, startup configuration validation, the command pipeline failure paths (throwing commands, database errors during permission checks), the quote and custom-response commands, the reminder commands (including the recurring form and the delivery engine), version formatting, the backup commands (trigger, list, two-step restore confirmation, maintenance-mode gating), the watchdog/health/notification surface (condition detection, alert cadence, `!health`, `!notifychannel`), presence and profile images, the slash surface (registration contract, virtual invocation, interactive help pagination), and the MCP endpoint (bearer auth, every tool over fakes, `run_command` capture/tier/cooldown/audit semantics, the per-guild agent tier).

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

Optional integrations are environment variables too: `SlashCommands__Enabled=true`, and `Mcp__Enabled=true` + `Mcp__ApiKey=<key>` (+ `-p 127.0.0.1:2525:2525` — the MCP server listens inside the container, so publish its port loopback-only like Compose does).

The image is secret-free: `appsecrets.json` is excluded from the build context (`.dockerignore`), so it can be pushed to a registry — secrets always come in via environment variables.

## Continuous integration & deployment

The repository ships a Jenkins pipeline ([`Jenkinsfile`](Jenkinsfile)): every build compiles the solution with warnings-as-errors and runs the unit tests, and — for version tags (`v<major.minor.patch>`) only — deploys the exact tested commit to the production server over SSH (build image, verify startup, auto-rollback on failure). See **[docs/jenkins-cicd.md](docs/jenkins-cicd.md)** for the full setup guide.

## Smoke test

Expected startup log sequence (from source, Compose, or plain Docker):

1. `Bot version: <version>` — the release tag semver plus the build commit's short hash (e.g. `1.2.3+1639324`; fallback `0.0.0+<hash>` on untagged commits)
2. `Database migrations applied successfully`
3. `Initializing Discord connection...`
4. Gateway ready (Discord.Net logs a `Ready` message; afterwards `Worker running at: ...` lines appear every 5 minutes).
5. The MCP state line: `MCP endpoint listening at ...` when enabled with a key, or `MCP endpoint disabled: ...` otherwise (never an error either way). With `SLASH_COMMANDS_ENABLED=true` you'll also see `Registered N application commands with Discord.` after the gateway connects.

Then, in a Discord channel the bot can see, send `!hello` — it should reply `Hello World`.

If the token is wrong/missing you'll instead see a critical message naming the missing keys, or `Discord: A supplied token was invalid.` — the bot keeps retrying, it does not crash.

## Commands & first-run permissions

| Command | Behavior |
|---|---|
| `!hello` | Replies "Hello World". |
| `!version` | Replies with the bot's version: the release tag semver plus the build commit's short hash (e.g. `1.2.3+1639324`). Works in DMs. |
| `!help [commandName]` | Lists the commands you can use, or shows detailed help (description, usage, required tier) for one command. |
| `!remindme <duration> <message...>` | Sets a personal reminder the bot sends you as a **DM** when it's due (durations like `10m`, `1d 12h`, `2w`; `!remindme every <duration> <message...>` repeats on that interval). Works in DMs. |
| `!reminders` / `!cancelreminder <number>` | Lists your pending reminders (soonest first) / cancels one by its number. Works in DMs. |
| `!quote [number\|text...]` | Shows a random quote of this server, a numbered one (`!quote 7`), or — with new text — saves that text as the server's next quote. |
| `!searchquotes <text...>` | Searches this server's quotes (up to 10 results). |
| `!removequote <number>` | Removes one of this server's quotes. |
| `!importquotes` | Imports the old bot's quotes: attach its `quoteSettings.json` to the message. |
| `!addresponse <name> <text...>` | Creates or updates a custom response for this server — afterwards `!<name>` replies with the stored text (dispatched like any command: same permission and cooldown checks). |
| `!listresponses` / `!removeresponse <name>` | Lists this server's custom response names / removes one. |
| `!commandpermission <name> [tier]` | Prints a command's required tier, or sets/creates it. Admin-only by default. |
| `!userpermission <user> [tier]` | Prints a user's tier, or sets/creates it. `<user>` is a raw Discord id or an @mention. Admin-only by default. |
| `!banuser <user> [duration]` | Bans a user from using the bot in this server — permanently, or temporarily with reminder-style duration tokens (`!banuser @user 1d 12h`). The banned user's invocations are ignored silently (no reply at all); DM features like reminders stay usable. Moderator-only by default; only users below your own tier, never the guild/bot owner. |
| `!unbanuser <user>` | Lifts a user's bot ban in this server. Moderator-only by default. |
| `!banlist` | Lists this server's current bot bans (user, lifetime, banned-by). Moderator-only by default. |
| `!settings` | Shows the server's effective command identifier and per-tier cooldown overrides. Admin-only by default. |
| `!settings commandidentifier <value>` / `reset` | Sets or clears the server's command identifier override (≤ 5 chars; `reset` reverts to the global default). |
| `!settings cooldown <tier> <seconds>` / `reset` | Sets or clears the server's per-tier cooldown override (tiers 0–3; `0` disables throttling for that tier). |
| `!agenttier [<tier 0-4>` / `clear]` | Shows, sets, or clears the tier the MCP operator agent acts at in this server — the agent can only run commands everyone can until a guild raises it. Owner-tier by default (guild owner or bot owner); the agent itself can never change its own ceiling. |
| `!health` | Shows the bot's operational state: version, uptime, gateway, database, pending reminders, backups, restore test, notification channel, MCP endpoint. Bot-owner tier; works in DMs. |
| `!presence [<type> <text...>` / `clear]` | Shows, sets, or clears the bot's activity (`playing`/`watching`/`listening`/`competing` + text ≤ 128 chars). Bot-owner tier; works in DMs; not persisted across restarts. |
| `!bio [<text...>` / `clear]` | Shows, sets, or clears the bot's profile "About Me" (≤ 190 chars, line breaks allowed), applied in every guild the bot is a member of. Bot-owner tier; works in DMs; not persisted across restarts. |
| `!avatar [<image-url>` / `reset]` | Shows, sets, or resets the bot's profile picture — attach exactly one image or pass a single image URL (PNG/JPEG/GIF, ≤ 10 MB; validated before anything changes). Bot-owner tier; works in DMs. |
| `!banner [<image-url>` / `clear]` | Shows, sets, or clears the bot's profile banner, same shape as `!avatar` but per guild. Bot-owner tier; works in DMs. |
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

Permission tiers and bot settings are **scoped per guild**: a user's tier or a command's required tier in one server has no effect in any other, and each server can override the global command identifier with `!settings commandidentifier <value>` (admins; `!settings commandidentifier reset` reverts to the global default). In **direct messages**, commands run in a DM scope: you act at owner tier and commands require their built-in default tiers — no per-server state is read or written. Commands that manage per-server state (`!userpermission`, `!commandpermission`, `!settings`, `!agenttier`, the bot-ban commands) are only available in servers.

Tiers are then managed at runtime, e.g.:

- `!userpermission @Someone 1` — grants moderator tier (as owner/admin).
- `!commandpermission hello 1` — restricts `!hello` to moderators and above.

