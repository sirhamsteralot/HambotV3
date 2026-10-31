# HambotV2

A Discord chat bot built on .NET 10 with [Discord.Net](https://github.com/DiscordNet/Discord.Net), EF Core 10 / Npgsql (PostgreSQL), and the .NET generic host.

## Quick start (Docker Compose)

```bash
cp .env.example .env   # fill in DISCORD_BOT_TOKEN and POSTGRES_PASSWORD
docker compose up -d --build
docker compose logs -f bot
```

Migrations apply automatically on startup. Stop with `docker compose down` (add `-v` to also delete the database data).

## Prerequisites

- Docker (with Compose plugin).
- .NET 10 SDK (`10.0.x`) — only for building from source or running tests.
- A Discord bot application (<https://discord.com/developers/applications>) with the **Message Content intent** enabled (Bot → Privileged Gateway Intents), invited with scopes `bot` + `applications.commands`:

```
https://discord.com/oauth2/authorize?client_id=<bot id>&scope=bot+applications.commands&permissions=8533303368350784
```

## Configuration

Non-secret settings in `appsettings.json`:

| Key | Default | Meaning |
|---|---|---|
| `BotConfiguration:BotName` | `HamBot` | Bot display name. |
| `BotConfiguration:LogFormat` | `{0}: {1}` | Format string for bridging Discord.Net log events. |
| `BotConfiguration:CommandIdentifier` | `!` | Command prefix (per-guild override via `!settings`). |
| `BotConfiguration:CooldownSecondsByTier` | `{ "0": 3, "1": 3, "2": 2, "3": 1 }` | Per-tier cooldown seconds; `0` disables throttling for that tier. |
| `BotConfiguration:Presence` / `BotConfiguration:Bio` | *(unset)* | Optional startup activity / profile bio; absent → left untouched. Invalid values fail startup. |
| `Llm:*` | see `HambotV2/appsettings.json` | Optional LLM features (automatic presence/bio updates). `Llm:ApiKey` is a secret — set via `LLM_API_KEY` in `.env` or `Llm__ApiKey`; absent → all LLM features inactive. |
| `Serilog` | Console + daily `logs/` file | Logging; changeable without rebuilding. |

Secrets (never commit; `.env` and `appsecrets.json` are gitignored):

| Key | Meaning |
|---|---|
| `Secrets:BotToken` | Discord bot token. |
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string. |
| `Mcp:Enabled` / `Mcp:ApiKey` / `Mcp:ListenUri` | Optional MCP endpoint; `ApiKey` required when enabled. |
| `Llm:ApiKey` | Optional Mistral API key for the LLM features; absent → inactive. |

- **Compose**: set `DISCORD_BOT_TOKEN` and `POSTGRES_PASSWORD` in `.env`; they reach the container as `Secrets__BotToken` / `ConnectionStrings__DefaultConnection` (env vars win over files).
- **From source / plain Docker**: put both in `HambotV2/appsecrets.json`:

  ```json
  {
    "Secrets": { "BotToken": "PlaceholderToken" },
    "ConnectionStrings": { "DefaultConnection": "Server=localhost:5432;Database=hambotdb;User Id=hambot;Password=PlaceholderPassword;" },
    "Mcp": { "Enabled": false, "ApiKey": "OptionalUnlessEnabled", "ListenUri": "http://0.0.0.0:2525" }
  }
  ```

  Use `localhost` from source, `host.docker.internal` in a plain container against host Postgres.

All required keys are validated at startup: missing keys or an unreachable database log a critical message and exit non-zero.

## Backups

The Compose `backup` sidecar (from `scripts/backup/`) dumps the database on schedule into the shared `backups` volume — independently of the bot. Configured via `.env`: `BACKUP_INTERVAL` (86400 s), `BACKUP_RETENTION` (7), `BACKUP_TEST_INTERVAL` (2592000 s), and optional offsite push (`BACKUP_RCLONE_REMOTE` + `BACKUP_S3_*`). Each pass: dump → validate → prune (only after a good dump) → push offsite if configured → write `backups/.backup-status` (read by `!health` and the watchdog). On the test interval it also restores the newest dump into a scratch database to prove restorability.

**Restore** — from Discord (healthy bot, bot-owner tier, DM-usable): `!backup` (on-demand dump), `!backups` (list), `!restorebackup <name>` then `!restorebackup <name> confirm` (maintenance mode while it runs). From the host (broken bot/database): `scripts/restore-backup.sh <dump-file>`.

## Health, watchdog & notifications

- `!health` (bot-owner tier, DMs) — version, uptime, gateway, database, pending reminders, backup/restore-test status, notification channel, MCP state, LLM state.
- `!notifychannel [<#channel> | clear]` (bot-owner tier, DMs) — sets the operator notification channel; persists across restarts; `NOTIFY_CHANNEL_ID` in `.env` is the default.
- With a channel set, command failures, retry-exhausted reminders, maintenance/restore outcomes, and watchdog alerts are posted there — rate-limited (default 5/min) with identical consecutive messages collapsed.
- The watchdog checks every 5 minutes and alerts the bot owner by DM (and the channel) on: gateway down > 10 min, failed/overdue backups or restore tests, LLM budget exhaustion or 3 consecutive LLM failures, maintenance mode, unreachable notification channel. Each condition alerts once, re-alerts hourly, announces recovery once. Without `BOT_OWNER_DISCORD_ID` it degrades to logs.

## LLM features (opt-in)

Set `LLM_API_KEY` (Mistral API key, a secret) in `.env` to activate; without it every LLM feature is inactive and `!llm status` / `!health` report so. Settings live under `Llm:*` in `appsettings.json` (see the table in `docs/software-design.md` §3.12): model, persona seed, chat-context bounds, daily request budget (default 500/UTC day), and the opt-in presence/bio update jobs (`LLM_PRESENCE_UPDATES_ENABLED`, `LLM_BIO_UPDATES_ENABLED`, each with its own `LLM_*_INTERVAL_MINUTES`). Phase 1 automatically refreshes the bot's presence and bio on their own intervals, in the persona's voice. Runtime controls: `!llm status`, `!llm off|on` (in-memory kill switch), `!llm update [presence|bio]` (run an update now, even with the jobs off — still gated by the kill switch/budget), `!model [list|<name>|clear]` (model override, validated against the provider's list), `!persona [<text...>|clear]` (global persona override) and `!settings persona <text...>|clear` (per-guild override). `!settings llmchat on|off` opts this server's recent chat into LLM update prompts (persisted per guild, off by default; DM-triggered updates never read chat). The watchdog alerts on budget exhaustion and on 3 consecutive LLM failures.

## MCP endpoint (agent API)

Opt-in MCP server ([Model Context Protocol](https://modelcontextprotocol.io)) over streamable HTTP, hosted in-process, bearer-key authenticated. Enable with `MCP_ENABLED=true` + `MCP_API_KEY` (generate: `openssl rand -hex 32`); it listens at `http://0.0.0.0:2525/mcp`, published loopback-only in Compose (`127.0.0.1:2525`), so clients on other machines need a tunnel.

Tools: `bot_status`, `list_guilds`, `list_channels(guildId)`, `list_commands`, `set_presence`, `set_bio`, `get_recent_messages(channelId, limit ≤ 50)`, `send_message(channelId, text ≤ 2000)`, `run_command(guildId, commandLine)` — a virtual invocation through the real command pipeline (same permissions, cooldowns, audit; the reply is returned as the tool result, never posted to a channel) — and the read-only member lookup tools: `search_users(guildId, query, limit ≤ 25)` (Discord member search by username/display name/nickname — resolve a name to a user id), `get_user_profile(guildId, userId)` (Discord identity plus the bot's records: permission tier, active bot ban, quotes added, last-modified custom responses; reminders stay private), `get_user_quotes(guildId, userId)` (≤ 50, newest first), and `get_user_messages(guildId, userId, limit ≤ 50)` (the member's recent messages across the bot's readable channels). The lookup tools write nothing and are not gated by `!agenttier`; during a backup restore they decline with the maintenance notice.

The agent acts at the lowest tier (0) per server; a guild owner or the bot owner raises it with `!agenttier <tier 0-4>`. The agent can never change its own ceiling.

## Slash commands

Opt-in via `SLASH_COMMANDS_ENABLED=true` (requires the `applications.commands` invite scope): the administrative/configuration commands (including `/llm`, `/model`, `/persona`), the reminder commands, `/searchquotes`, `/version`, and `/help` run through the same command pipeline with identical permissions and cooldowns, but every reply is ephemeral (visible only to the invoker); multi-page `/help` and `/searchquotes` replies carry previous/next buttons. Community-facing commands (`!quote` & co., custom responses) stay text-only. Global commands can take up to an hour to appear.

## Building & running locally

```bash
docker run -d --name hambot-postgres -e POSTGRES_USER=hambot -e POSTGRES_PASSWORD=PlaceholderPassword \
  -e POSTGRES_DB=hambotdb -p 5432:5432 postgres:17
# configure HambotV2/appsecrets.json (Server=localhost:5432), then:
dotnet build HambotV2.sln
dotnet run --project HambotV2
dotnet test HambotV2.sln   # no database, token, or Docker needed
```

## Production

Same as the quick start, on your server. Data persists in the `db-data` and `backups` volumes; all services use `restart: unless-stopped`; the bot waits for the database healthcheck. Plain Docker also works (build context is the repo root):

```bash
docker build -f HambotV2/Dockerfile -t hambotv2:latest .
docker run -d --name hambot --restart unless-stopped \
  -e Secrets__BotToken=<token> \
  -e "ConnectionStrings__DefaultConnection=Server=<host>;Port=5432;Database=hambotdb;User Id=hambot;Password=<password>;" \
  hambotv2:latest
```

The image is secret-free (`appsecrets.json` is `.dockerignore`d). CI: the Jenkins pipeline builds and tests every push and auto-deploys version tags — see [docs/jenkins-cicd.md](docs/jenkins-cicd.md).

## Smoke test

Expected startup logs: `Bot version: <version>` → `Database migrations applied successfully` → `Initializing Discord connection...` → gateway `Ready` → the MCP state line and the LLM state line (and, with slash enabled, `Registered N application commands with Discord.`). Then `!hello` in a channel the bot can see should reply `Hello World`. A wrong token logs a critical message and the bot keeps retrying — it does not crash.

## Commands

| Command | Behavior |
|---|---|
| `!hello` / `!version` | "Hello World" / version string (semver + commit hash). DM-usable. |
| `!help [name \| page]` | Lists usable commands grouped by category, paginated with previous/next buttons, or detailed help for one command. |
| `!remindme <duration> <message...>` | Personal reminder, delivered as a DM. `!remindme every <duration> ...` recurs. DM-usable. |
| `!reminders` / `!cancelreminder <n>` | List (soonest first) / cancel your reminders. DM-usable. |
| `!quote [number\|text...]` | Random / numbered quote, or add new text as the next quote. |
| `!searchquotes <text...>` / `!removequote <n>` | Search this server's quotes, most relevant first — substring hits by quote number, then typo-tolerant word matches (≤ 2 edits) — up to 100 results, 10 per page with previous/next buttons. `!removequote` is moderator tier. |
| `!importquotes` | Import the old bot's `quoteSettings.json` (attached). Bot-owner tier. |
| `!quoteoftheday [<due> <interval> \| channel <#ch\|clear> \| template <text...>]` | Scheduled quote publication into a channel, all UTC. Admin tier. |
| `!addresponse <name> <text...>` / `!listresponses` / `!removeresponse <name>` | Manage custom responses (moderator tier; `!listresponses` everyone). |
| `!commandpermission <name> [tier]` / `!userpermission <user> [tier]` | Show/set command or user tier. Admin tier. |
| `!banuser <user> [duration]` / `!unbanuser <user>` / `!banlist` | Per-guild bot bans; banned users are ignored silently. Moderator tier. |
| `!settings [commandidentifier <v>\|reset \| cooldown <tier> <s>\|reset \| persona <text...>\|clear \| llmchat on\|off]` | Guild identifier, per-tier cooldown overrides, this server's LLM persona override, and the per-guild opt-in for LLM chat context. Admin tier. |
| `!llm [status\|off\|on\|update [presence\|bio]]` | LLM usage status; off/on toggles the in-memory kill switch (not persisted); update triggers an LLM presence/bio update now. Bot-owner tier; DM-usable. |
| `!persona [<text...>\|clear]` | Show/set/clear the global LLM persona override (persisted). Admin tier; DM-usable. |
| `!model [list\|<name>\|clear]` | Show the effective LLM model, list the provider's models, or set/clear the runtime model override (validated against the API). Admin tier; DM-usable. |
| `!agenttier [<tier 0-4>\|clear]` | Tier the MCP operator agent acts at here. Owner tier. |
| `!health` / `!notifychannel [<#ch>\|clear]` / `!backup` / `!backups` / `!restorebackup <name> [confirm]` | Bot-owner tier; all DM-usable. |
| `!presence [<type> <text...>\|clear]` / `!bio [<text...>\|clear]` | Activity ("About Me"). Bot-owner tier; not persisted across restarts. |
| `!avatar [<image-url>\|reset]` / `!banner [<image-url>\|clear]` | Profile images (PNG/JPEG/GIF ≤ 10 MB; attach one image or pass a URL). Bot-owner tier. |

Permission tiers: 0 everyone · 1 moderator · 2 admin · 3 owner (held automatically by the guild owner) · 4 bot owner (configured via `BOT_OWNER_DISCORD_ID`; tier 4 in every guild, never assignable via commands).

- **Fresh guild bootstrap**: the guild owner already acts at owner tier; permission-management commands are admin-tier with an escalation guard (you can only assign tiers strictly below your own), so nobody can self-promote.
- **Scoping**: tiers and settings are per guild. In **DMs** you act at owner tier against built-in default tiers; commands that manage guild state (`!userpermission`, `!commandpermission`, `!settings`, `!agenttier`, the ban commands) decline in DMs.
- **Typos**: an unknown command (or custom response) that looks like a runnable one gets a `Did you mean !<name>?` suggestion — edit distance ≤ 2 or a ≥ 3-character prefix, filtered to commands you can actually run so higher-tier names are never revealed.
