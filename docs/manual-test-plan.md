# Manual Test Plan — pre-production command testing

Run this against a **test deployment** before rolling a build out to production: a separate Discord application (own token), a throwaway test server, and its own `.env` + Docker volumes. Never point the test stack at the production database, and never run the destructive steps (§3, §8) against production.

**Why a separate bot application:** permission tiers, guild settings, quotes, and reminders all live in the database, but the *bot identity* (token, bot owner id, notification channel) comes from configuration — sharing a token between test and prod means every test command runs in prod's name. A second application on the same machine costs nothing: same `docker-compose.yml`, different `.env`.

## 0. Test environment setup

1. Create a second Discord application with a bot user; enable the **Message Content intent**; copy the token.
2. Create a test server with at least: a normal channel, a second channel to use as the notification channel, and a scratch category for permission tests.
3. Invite the bot with the standard permissions (it needs View Channel + Send Messages at minimum; also Attach Files for the quote-import test).
4. Give the test `.env` these overrides (they shrink every wait to minutes instead of hours/days):

   ```dotenv
   DISCORD_BOT_TOKEN=<test token>
   BOT_OWNER_DISCORD_ID=<your Discord id>
   NOTIFY_CHANNEL_ID=<test notification channel id>

   # fast watchdog cadence for testing (defaults: 5 / 10 / 60)
   WATCHDOG_CHECK_INTERVAL_MINUTES=1
   WATCHDOG_GATEWAY_DOWN_ALERT_MINUTES=1
   WATCHDOG_RE_ALERT_MINUTES=2

   # fast backup cadence for testing (defaults: 86400 / 7 / 2592000)
   BACKUP_INTERVAL=60
   BACKUP_RETENTION=3
   BACKUP_TEST_INTERVAL=300
   ```

5. Bring it up: `docker compose up -d --build`. Verify the startup lines: `Bot version:`, `Database migrations applied successfully`, `Initializing Discord connection...`, then `Gateway: Ready`.
6. Useful observation commands while testing:

   ```bash
   docker compose logs -f bot                     # bot + watchdog + notification logs
   docker compose logs -f backup                  # sidecar passes
   docker compose exec backup ls -l /backups      # dump files, retention behavior
   docker compose exec backup cat /backups/.backup-status   # the status file !health reads
   ```

**Reminder:** if the `db-data` volume already exists from an earlier `.env`, changing `POSTGRES_PASSWORD` has no effect — postgres only applies it on first init. Either keep the old password or wipe the volume (`docker compose down -v`) while it is still disposable.

## 1. Smoke test

| # | Action | Expected |
|---|---|---|
| 1.1 | `!version` in the test server | Reply with the version (e.g. `1.2.3+1639324`; local builds show `0.0.0+unknown` — fine). |
| 1.2 | `!hello` | `Hello World` |
| 1.3 | `!help` | Lists all commands, one per line, with descriptions. |
| 1.4 | `!help backup` | Detailed help: description, usage, required tier. |
| 1.5 | `!help nonexistentcommand` | Generic "The requested command is unavailable" — no error, no reveal. |
| 1.6 | Any command from a **second, non-owner test account** (default tier) | `You are not permitted to use that command` for admin/owner-tier commands. |

## 2. Backups — create & list (FR-96/FR-97)

| # | Action | Expected |
|---|---|---|
| 2.1 | `!backups` (before any backup) | Either "There are no backups yet." or the sidecar's scheduled dumps (already running every 60 s in the test env). |
| 2.2 | `!backup` | "Backup created: hambotv2-<timestamp>.dump (<size>)" within a few seconds. |
| 2.3 | `!backups` | New dump listed, newest first; timestamp and size match. |
| 2.4 | `docker compose exec backup ls /backups` | The file exists in the volume — the command and the sidecar share one directory. |
| 2.5 | Second account runs `!backup` / `!backups` | Permission denied (tier 4 default). |
| 2.6 | `!backup` in a **DM** with the bot owner account | Works — these commands are DM-usable. |
| 2.7 | Retention: wait for >3 sidecar dumps (`BACKUP_INTERVAL=60`), check `ls /backups` | Only the newest `BACKUP_RETENTION=3` dump files remain. |

## 3. Restore & maintenance mode (FR-98) — the destructive core path

Set this up so the restore is *provably real*: make data, back it up, change data, restore, see the old data return.

1. `!quote add test-marker-before-backup` (or create any data: a quote, a custom response).
2. `!backup` → note the dump name.
3. `!quote remove 1` (delete/modify the marker data).
4. `!restorebackup <name>` (without `confirm`) → **warning only**, nothing restored: "…will REPLACE ALL CURRENT DATA… run: !restorebackup <name> confirm".
5. Verify the marker is still deleted (the unconfirmed step changed nothing).
6. `!restorebackup <name> confirm` → success reply: "Backup '<name>' restored successfully. The bot is back in normal operation."
7. `!quote 1` (fetch the marker) → **it's back** — the restore actually replaced the data.
8. While a large restore runs (hard to time manually — the test DB is small), any other command should get "The bot is in maintenance mode…" — if you miss the window, the two notification posts in the notification channel (§3.11) are the evidence the mode was entered and exited.
9. Negative: `!restorebackup does-not-exist` → "There is no backup named 'does-not-exist' — run !backups…".
10. Negative: `!restorebackup <name> wrongword` → asks again for the exact `confirm` suffix.

## 4. `!health` (FR-105)

| # | Action | Expected |
|---|---|---|
| 4.1 | `!health` in the server (bot owner) | One report: version, uptime, gateway state (`Connected`), database (`reachable`), pending reminders count, backup line (timestamp + name + size), restore test line (timestamp — `passed`), notification channel (`<#id> (…)`). |
| 4.2 | `!health` in a **DM** | Same report — DM-usable. |
| 4.3 | Second account runs `!health` | Permission denied (tier 4). |
| 4.4 | Correlate with reality: compare the backup line against `cat /backups/.backup-status` | Timestamps match the sidecar's status file. |

## 5. `!notifychannel` (FR-117)

| # | Action | Expected |
|---|---|---|
| 5.1 | `!notifychannel` (no args) | Shows the effective channel — initially the `NOTIFY_CHANNEL_ID` from `.env`. |
| 5.2 | `!notifychannel <#other-channel>` (mention a different channel in the same server) | Confirmation reply; subsequent notifications land in the new channel. |
| 5.3 | `docker compose restart bot`, then `!notifychannel` | Still shows the runtime override — it persists in the database across restarts. |
| 5.4 | `!notifychannel clear`, then `!notifychannel` | Falls back to the `.env` default again (not "not set" — the env default still applies). |
| 5.5 | Remove `NOTIFY_CHANNEL_ID` from `.env`, `docker compose restart bot`, `!notifychannel` | "not set (notifications go to logs and bot-owner DMs)". |
| 5.6 | Second account runs `!notifychannel <#channel>` | Permission denied (tier 4). |

## 6. Notifications in action (FR-118)

| # | Action | Expected |
|---|---|---|
| 6.1 | Trigger a command failure: `!quote` with a bad argument (e.g. `!quote abc` where a number is required) → check channel | The generic in-channel error goes to the user; nothing sensitive is posted. For a real failure path, use a command that throws (e.g. stop the db container briefly and run any DB-backed command). |
| 6.2 | **Rate limit / collapse:** while the db is stopped, spam several DB-backed commands | The user channel gets the generic error each time; the notification channel gets at most 5 posts per minute and identical consecutive messages collapse to one — no flood. Overflow still visible in `docker compose logs bot`. |
| 6.3 | Restart the db | The bot recovers on its own; verify a normal command works again. |

## 7. Watchdog (FR-106)

Watchdog DMs go to `BOT_OWNER_DISCORD_ID` **and** post to the notification channel. With the fast test cadence (1-min checks, 1-min gateway threshold, 2-min re-alert) each case resolves in minutes. DM delivery requires the bot to share a server with the owner account.

| # | Action | Expected |
|---|---|---|
| 7.1 | **Gateway down:** put an invalid token in `.env`, `docker compose restart bot` | Gateway never connects (`Gateway: Disconnected`/retry logs); after ~2 min: owner DM + notification post "the Discord gateway has been disconnected". It re-alerts every ~2 min while it persists. Restore the real token and restart → recovery announcement ("the Discord gateway is connected again") exactly once. |
| 7.2 | **Backup overdue:** `docker compose stop backup` and wait ~2 min (interval 60 s + grace) | Alert: "no successful backup has completed within the scheduled interval". `docker compose start backup` → recovery announcement once. |
| 7.3 | **Restore test failed/overdue:** set `BACKUP_TEST_INTERVAL=120`, stop the db for a few minutes so a scheduled pass fails validation, restart db | If a restore test fails you get the failed alert; if tests stop running (sidecar stopped) you get the overdue alert once it passes the interval. Recovery announcement when tests pass again. |
| 7.4 | **Notification channel unreachable:** set `!notifychannel <#a-channel-the-bot-cannot-see>` (or drop the bot's permissions on the current one) | Within a couple of check intervals: owner DM "the notification channel could not be posted to" (the meta-failure path). Fix the channel perms → recovery announcement. |
| 7.5 | **Maintenance mode:** during §3's confirmed restore | Watchdog alert "the bot is in maintenance mode" on entry, recovery announcement on exit. |
| 7.6 | **No owner fallback:** remove `BOT_OWNER_DISCORD_ID`, restart, stop the backup container | No DMs (nobody to DM); alerts degrade to `docker compose logs bot` lines only. Restore the id afterwards. |

## 8. Operator restore drill (FR-83 / NFR-19) — the scripted path

This is the outstanding operator action; run it once here and record the outcome. It proves recovery when the *bot itself* is broken.

1. Note a dump from `!backups` and copy it out of the volume:
   ```bash
   docker compose cp backup:/backups/<dump-name>.dump ./test-restore.dump
   ```
2. Break the bot on purpose (e.g. invalid token again — simulates a bot that cannot start).
3. Run the drill against the **test stack only**:
   ```bash
   scripts/restore-backup.sh ./test-restore.dump
   ```
4. Expected: the script validates the dump, stops the bot, drops/recreates the database, restores, restarts, and prints success after matching the known startup lines (`Bot version:`, `Database migrations applied successfully`, `Initializing Discord connection...`, gateway ready).
5. Verify data: the restored marker quote from §3 is present again; `!health` reports `reachable`.

**Record the outcome** (date, dump, both §3 and §8 results) — with that, the restore drill (NFR-19) is satisfied for this build.

## 9. Regression pass — pre-existing features

Quick sweep to catch anything the new pipeline changes (maintenance gate, notification hooks, new `BotSetting` entity) broke:

| Area | Commands | Expected |
|---|---|---|
| Permissions | `!userpermission @user 1`, `!userpermission @user 5`, `!commandpermission hello 0` | Grant works from owner/admin; tier 5 rejected (max 4); self/equal-tier escalation rejected. |
| Guild settings | `!settings`, `!settings commandidentifier ?`, `!settings commandidentifier reset`, `!settings cooldown 0 5` | Show/set/reset behave; new identifier applies to the very next message; DMs decline ("only available in servers"). |
| Cooldowns | Spam `!hello` as tier-0 user after `!settings cooldown 0 5` | Throttle reply with wait seconds; no throttle for the bot owner (tier 4). |
| Quotes | `!quote add/list/remove/get random/search` + `!importquotes` with a small JSON attachment | Full lifecycle; import continues numbering; per-guild isolation (a quote made in test server A is invisible in server B). |
| Custom responses | `!response add test ping`, then `!test` | Custom response fires through the permission/cooldown path; unknown-name fallback reply otherwise. |
| Reminders | `!remindme 1m test`, then wait | DM delivery of the reminder; retry/defer behavior unchanged. |
| Recurring reminders | `!remindme every 10m test`, `!reminders`, wait two intervals, `!cancelreminder <n>` | Recurring confirmation; list entry marked `repeats every 10m`; a DM per interval (each re-scheduled from its delivery); cancellation stops it. |

## 10. Rollout checklist

Before promoting the same build to production:

- [ ] §1–§9 pass (§3 and §8 recorded as the restore drill, NFR-19).
- [ ] Production `.env` reviewed: keep the **production** intervals (`BACKUP_INTERVAL=86400`, `RETENTION=7`, `TEST_INTERVAL=2592000`, watchdog defaults) — do not ship the fast test values.
- [ ] `BACKUP_RCLONE_REMOTE` set in production if offsite push is wanted (plus S3 credentials) — otherwise leave empty.
- [ ] Production `NOTIFY_CHANNEL_ID` points at a channel the prod bot can actually see (the §7.4 failure is exactly what a misconfigured channel looks like).
- [ ] Verify in prod after deploy: startup lines, first scheduled backup lands (`docker compose logs backup`), `!health` shows the prod dump.

## 11. Presence & profile identity (FR-78–FR-80, FR-113, FR-115–FR-116)

Requires a second test server the bot is a member of (per-guild applies are visible there), and two small image files (a PNG under 10 MB and a text file that is not an image).

| # | Action | Expected |
|---|---|---|
| 11.1 | `!presence watching the test server` in the server | Bot's activity changes to "Watching the test server" immediately; reply confirms. |
| 11.2 | `!presence` | "Current activity: watching the test server". |
| 11.3 | `!presence nonsense text` / `!presence playing` (no text) | In-channel errors; activity unchanged. |
| 11.4 | `!presence clear` | Activity removed. |
| 11.5 | `!bio line one` (message with a line break in the text) | Bio applies with the line break preserved; visible on the bot's per-server profile in **both** servers. |
| 11.6 | `!bio` / `!bio clear` | Show reports the current bio; clear removes it everywhere. |
| 11.7 | `!avatar` with the PNG attached | Confirmation reply; profile picture changes in both servers and in DMs (global avatar). |
| 11.8 | `!avatar <image-url>` with a direct image link | Same result via the URL path. |
| 11.9 | `!avatar` with the text file attached, and `!avatar not-a-url` | In-channel rejection (not a PNG/JPEG/GIF; not an image URL); nothing changes. |
| 11.10 | `!avatar reset` / `!banner clear` | Custom images removed; avatar falls back to the application default; `!banner` reports "No banner is set." |
| 11.11 | Second account runs `!presence playing chess` | Permission denied (tier 4). |
| 11.12 | Same commands in a **DM** | All work — DM-usable (no guild state). |
| 11.13 | Configure `BotConfiguration:Presence:Type`/`:Text` (and optionally `BotConfiguration:Bio`), restart the bot | The configured activity (and bio) apply once the gateway is ready; a runtime change made before the restart is gone — runtime changes are not persisted. |
| 11.14 | Set `BotConfiguration:Presence:Type` to an invalid value, restart | Startup fails fast with a critical log line naming `BotConfiguration:Presence:Type`. |

## 12. Slash commands (FR-110–FR-112 / NFR-26)

Set `SlashCommands__Enabled: true` (or `SLASH_COMMANDS_ENABLED=true` in Compose) and restart the bot; the log line "Registered N application commands with Discord." confirms the registration pass. Requires the test account to have the commands visible (global commands can take up to an hour to propagate in the Discord client; re-login refreshes them).

| # | Action | Expected |
|---|---|---|
| 12.1 | Type `/` in the test server | The bot's `/` picker shows exactly the administrative/configuration, user-scoped, and help commands (agenttier, backup, backups, bio, cancelreminder, commandpermission, health, help, notifychannel, presence, remindme, reminders, restorebackup, settings, userpermission, avatar, banner) — **no** quote, response, or `version` commands (FR-110/FR-119). |
| 12.2 | `/remindme` with arguments `10m water the plants` | Confirmation reply identical to the text surface, visible **only to the invoker** (ephemeral, FR-112). |
| 12.3 | `/reminders` | Lists your pending reminders, visible only to the invoker. |
| 12.4 | `/userpermission` picking a user and tier 1 (as guild owner) | Same reply as `!userpermission <@user> 1`; the tier is set. |
| 12.5 | Second account runs `/backup` (tier 4 required) | Denial visible **only to the invoker** (ephemeral); no public reply (FR-112). |
| 12.6 | `/backup` twice within the cooldown window as an allowed user | Ephemeral throttle reply naming the wait; the text surface shows the same shared throttle afterwards (one key per user per command across surfaces). |
| 12.7 | `/remindme 10m test`, then `!remindme 10m test` again within the window | The text invocation is throttled — slash and text share the cooldown (NFR-12). |
| 12.8 | `/help` as the bot owner (tier 4) | Page 1 of the command list appears with a `page 1 of N` header and previous/next buttons, visible only to the invoker; the previous button is disabled (FR-119). |
| 12.9 | Click **Next ▶** on the `/help` reply | The same ephemeral message updates in place to page 2 with rebuilt buttons — no new message (FR-119). |
| 12.10 | Type `!help` in the text channel (bot owner) | The paginated list carries the same buttons on a plain text message; clicking them pages it the same way, regardless of `SlashCommands:Enabled` (FR-119). |
| 12.11 | Spam the pagination buttons faster than the tier cooldown | Ephemeral throttle reply naming the wait; the paginated message stays unchanged — paging shares the command cooldown (FR-111 semantics). |
| 12.8 | `/userpermission` in a **DM** | "This command manages per-server state and is only available in servers" — the DM scope applies (FR-25). `/reminders` in a DM works. |
| 12.9 | `/restorebackup <name>` without confirm | The destructive-restore warning, same as the text surface. |
| 12.10 | Set `SlashCommands__Enabled: false`, restart | Nothing registers; the `/` picker keeps stale commands until the next enabled boot's bulk overwrite removes them (the overwrite runs only when enabled — re-enable once before production to clear). Text surface unaffected throughout. |

## 13. MCP endpoint (FR-86–FR-91 / FR-120 / NFR-20)

Set `MCP_ENABLED=true` and `MCP_API_KEY=<key>` in `.env` (or `Mcp__Enabled`/`Mcp__ApiKey` as environment variables), then restart the bot. The startup log must name the state and address; with a key: "MCP endpoint listening at http://0.0.0.0:2525/mcp". Reach the endpoint from the deployment host only (`http://127.0.0.1:2525/mcp` — the compose port is loopback-published).

Any MCP-capable client works; with raw HTTP, each call is a POST with the headers `Authorization: Bearer <key>`, `Accept: application/json, text/event-stream`, and a JSON-RPC body (`initialize` first, then `tools/call`).

| # | Action | Expected |
|---|---|---|
| 13.1 | Start with `MCP_ENABLED=true` but **no** key | Log: "MCP endpoint disabled: Mcp:Enabled is true but no Mcp:ApiKey is configured"; nothing listens on 2525; `!health` shows the same state. |
| 13.2 | Start with `MCP_ENABLED=false` | Log: "MCP endpoint disabled: Mcp:Enabled is false"; `!health` reports it. |
| 13.3 | POST without the `Authorization` header | 401 — rejected before any tool logic (FR-87). |
| 13.4 | POST with a wrong key | 401. |
| 13.5 | `bot_status` | Version, uptime, gateway state, guild count, presence, bio — all matching the live bot. |
| 13.6 | `list_guilds` | The test server's id and name. |
| 13.7 | `list_commands` | Every registered command with tier, description, and usage, in name order. |
| 13.8 | `get_recent_messages` on a channel the bot can read (limit 5) | The 5 most recent messages, newest first, ≤ 50. |
| 13.9 | `send_message` to the test channel | The plain-text message appears in the channel. |
| 13.10 | `run_command` with a tier-0 command line, e.g. `hello` | The command's reply returned as the tool result; **nothing** posted to any channel. |
| 13.11 | `run_command` with a tier-4 command line (e.g. `health`) on a fresh guild | "You are not permitted to use that command" — the default agent tier is the lowest (FR-120). |
| 13.12 | `!agenttier` (guild owner) | "The operator agent acts at the default, tier 0 (everyone), in this server; raise it with a tier 1-4". |
| 13.13 | `!agenttier 4`, then `run_command` with a tier-4 command line again | The command executes; its reply is the tool result. |
| 13.14 | `!agenttier 1`, then `run_command` with a tier-2+ command line | Denied again — the per-guild tier governs the agent. |
| 13.15 | `run_command` with the command line `agenttier 4` | "The operator agent cannot change its own tier in this server" — the agent can never raise its own ceiling (FR-120). |
| 13.16 | `!agenttier 4` from a second, non-owner account | "You are not permitted to use that command" (owner tier required). |
| 13.17 | `!agenttier clear` | Reset to the lowest-tier default; `!agenttier` shows it. |
| 13.18 | `!agenttier` in a **DM** | "This command manages per-server settings and is only available in servers" (FR-25). |
| 13.19 | `!health` | The MCP line shows "listening at http://0.0.0.0:2525/mcp" (or the disabled state) — the endpoint state joins the report (FR-105). |
| 13.20 | `/agenttier` (slash surface on) | Same show/set/clear behavior as the text surface, reply visible only to the invoker. |
