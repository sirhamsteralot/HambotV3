# Manual Test Plan — pre-production command testing

Run against a **test deployment**: a second Discord application (own token), a throwaway server, its own `.env` + volumes. Never point the test stack at the production database and never run the destructive steps (§3, §8) against production. Sharing a token with prod would mean every test command runs in prod's name.

## 0. Test environment setup

1. Create a second Discord application with the **Message Content intent**; create a test server with a normal channel, a notification channel, and a scratch category.
2. Invite the bot (View Channel + Send Messages minimum; Attach Files for the quote-import test).
3. Test `.env` — fast cadences so everything resolves in minutes:

   ```dotenv
   DISCORD_BOT_TOKEN=<test token>
   BOT_OWNER_DISCORD_ID=<your Discord id>
   NOTIFY_CHANNEL_ID=<test notification channel id>
   WATCHDOG_CHECK_INTERVAL_MINUTES=1
   WATCHDOG_GATEWAY_DOWN_ALERT_MINUTES=1
   WATCHDOG_RE_ALERT_MINUTES=2
   BACKUP_INTERVAL=60
   BACKUP_RETENTION=3
   BACKUP_TEST_INTERVAL=300
   # Optional, for §16 (LLM phase 1) — without LLM_API_KEY every LLM feature is inactive:
   LLM_API_KEY=
   LLM_PRESENCE_UPDATES_ENABLED=
   LLM_PRESENCE_INTERVAL_MINUTES=
   LLM_BIO_UPDATES_ENABLED=
   LLM_BIO_INTERVAL_MINUTES=
   LLM_DAILY_REQUEST_BUDGET=
   ```

4. `docker compose up -d --build`; verify startup: `Bot version:`, `Database migrations applied successfully`, `Initializing Discord connection...`, `Gateway: Ready`.
5. Observation commands: `docker compose logs -f bot`, `docker compose logs -f backup`, `docker compose exec backup ls -l /backups`, `docker compose exec backup cat /backups/.backup-status`.

Note: `POSTGRES_PASSWORD` only applies on first volume init — keep the old password or wipe the volume while it's disposable.

## 1. Smoke test

| # | Action | Expected |
|---|---|---|
| 1.1 | `!version` | Version string (local builds `0.0.0+unknown` is fine). |
| 1.2 | `!hello` | `Hello World` |
| 1.3 | `!help` | Commands grouped under bold category headers, alphabetized within groups; empty groups omitted. |
| 1.4 | `!help backup` | Detailed help (description, usage, tier). |
| 1.5 | `!help nonexistentcommand` | Generic "The requested command is unavailable". |
| 1.6 | Any admin/owner command from a second, default-tier account | "You are not permitted to use that command". |

## 2. Backups — create & list

| # | Action | Expected |
|---|---|---|
| 2.1 | `!backups` (before any backup) | Empty message, or the sidecar's scheduled dumps. |
| 2.2 | `!backup` | "Backup created: hambotv2-<timestamp>.dump (<size>)" within seconds. |
| 2.3 | `!backups` | New dump listed newest first. |
| 2.4 | `docker compose exec backup ls /backups` | The file exists — commands and sidecar share one directory. |
| 2.5 | Second account runs `!backup` | Permission denied (tier 4). |
| 2.6 | `!backup` in a DM | Works — DM-usable. |
| 2.7 | Wait for >3 sidecar dumps | Only `BACKUP_RETENTION=3` files remain. |

## 3. Restore & maintenance mode — the destructive core path

1. `!quote add test-marker-before-backup` (or create any data).
2. `!backup` → note the dump name.
3. `!quote remove 1` (delete the marker).
4. `!restorebackup <name>` *without* `confirm` → warning only; verify the marker is still deleted.
5. `!restorebackup <name> confirm` → success reply.
6. `!quote 1` → **the marker is back** — the restore really replaced the data.
7. During the restore, other commands get the maintenance notice; the notification channel posts maintenance entry/exit (evidence if the window was too fast to catch).
8. Negative: `!restorebackup does-not-exist` → "no backup named…" error; `!restorebackup <name> wrongword` → asks for the exact `confirm` suffix.

## 4. `!health`

| # | Action | Expected |
|---|---|---|
| 4.1 | `!health` (bot owner, in server) | One report: version, uptime, gateway `Connected`, database `reachable`, pending reminders, backup + restore-test lines, notification channel. |
| 4.2 | `!health` in a DM | Same report. |
| 4.3 | Second account | Permission denied (tier 4). |
| 4.4 | Compare the backup line to `cat /backups/.backup-status` | Timestamps match. |

## 5. `!notifychannel`

| # | Action | Expected |
|---|---|---|
| 5.1 | `!notifychannel` | Shows the `NOTIFY_CHANNEL_ID` default. |
| 5.2 | `!notifychannel <#other-channel>` | Confirmation; notifications land in the new channel. |
| 5.3 | `docker compose restart bot`, then `!notifychannel` | The runtime override persists across restarts. |
| 5.4 | `!notifychannel clear` | Falls back to the `.env` default. |
| 5.5 | Remove `NOTIFY_CHANNEL_ID` from `.env`, restart | "not set (notifications go to logs and bot-owner DMs)". |
| 5.6 | Second account | Permission denied. |

## 6. Notifications in action

| # | Action | Expected |
|---|---|---|
| 6.1 | Cause a real failure (stop the db container, run any DB-backed command) | Generic in-channel error; the notification channel gets the failure post. |
| 6.2 | While the db is stopped, spam DB-backed commands | Notification channel: ≤ 5 posts/min, identical consecutive messages collapsed; overflow still in `docker compose logs bot`. |
| 6.3 | Restart the db | The bot recovers on its own. |

## 7. Watchdog

Alerts go to `BOT_OWNER_DISCORD_ID` by DM **and** the notification channel (DM delivery needs a shared server). With the fast cadence each case resolves in minutes.

| # | Action | Expected |
|---|---|---|
| 7.1 | **Gateway down**: invalid token in `.env`, restart bot | After ~2 min: alert "the Discord gateway has been disconnected", re-alerting every ~2 min; restore the token → recovery announced once. |
| 7.2 | **Backup overdue**: `docker compose stop backup` ~2 min | Alert "no successful backup…"; `start backup` → recovery once. |
| 7.3 | **Restore test failed/overdue**: `BACKUP_TEST_INTERVAL=120`, stop the db for a pass, restart | The failed or overdue restore-test alert; recovery when tests pass again. |
| 7.4 | **Channel unreachable**: `!notifychannel <#channel-the-bot-cannot-see>` | Owner DM "the notification channel could not be posted to"; fix perms → recovery. |
| 7.5 | **Maintenance mode**: during §3's restore | Alert on entry, recovery on exit. |
| 7.6 | **No owner**: remove `BOT_OWNER_DISCORD_ID`, stop backup | No DMs; alerts degrade to logs. Restore the id afterwards. |

## 8. Operator restore drill — the scripted path

Proves recovery when the *bot itself* is broken. Run once here and record the outcome (this satisfies the restore drill).

1. Copy a dump out: `docker compose cp backup:/backups/<dump-name>.dump ./test-restore.dump`
2. Break the bot on purpose (invalid token again).
3. `scripts/restore-backup.sh ./test-restore.dump` (test stack only).
4. Expected: validates, stops the bot, drops/recreates the database, restores, restarts, prints success after matching the startup lines.
5. Verify: the §3 marker quote is present; `!health` reports `reachable`.

## 9. Regression pass — pre-existing features

| Area | Commands | Expected |
|---|---|---|
| Permissions | `!userpermission @user 1` / `@user 5`; `!commandpermission hello 0` | Grant works; tier 5 rejected (max 4); self/equal-tier escalation rejected. |
| Guild settings | `!settings`, `!settings commandidentifier ?`, `… reset`, `!settings cooldown 0 5` | Set applies to the very next message; DMs decline. |
| Cooldowns | Spam `!hello` as tier-0 user | Throttle reply with wait seconds; no throttle for the bot owner. |
| Quotes | Lifecycle + `!importquotes` with a small JSON attachment | Full lifecycle; import continues numbering; per-guild isolation across servers. |
| Custom responses | `!addresponse test ping`, then `!test` | Fires through the permission/cooldown path; unknown names fall back to "unavailable". |
| Suggestions | `!quoe` (typo), `!zzqxxv` (garbage) | The typo gets "Did you mean !quote?" appended; the garbage gets the plain "unavailable" reply. |
| Fuzzy search | `!searchquotes <misspelled word of a known quote>` | The quote is still found; exact substring matches rank ahead of fuzzy ones. |
| Reminders | `!remindme 1m test` | DM delivery. |
| Recurring | `!remindme every 10m test`, wait two intervals, `!cancelreminder <n>` | List entry marked recurring; a DM per interval; cancellation stops it. |

## 10. Rollout checklist

- [ ] §1–§16 pass (§3 and §8 recorded as the restore drill).
- [ ] Production `.env` uses the **production** intervals (`BACKUP_INTERVAL=86400`, `RETENTION=7`, `TEST_INTERVAL=2592000`, watchdog defaults) — not the fast test values.
- [ ] `BACKUP_RCLONE_REMOTE` set in production if offsite push is wanted.
- [ ] Production `NOTIFY_CHANNEL_ID` points at a channel the prod bot can see.
- [ ] Production `.env` decides the LLM features deliberately: leave `LLM_API_KEY` empty to keep them inactive, or set it (plus the `LLM_*` job flags/budget) — the watchdog then also watches budget exhaustion and consecutive LLM failures.
- [ ] After deploy: startup lines, first scheduled backup lands, `!health` shows the prod dump.

## 11. Presence & profile identity

Needs a second test server the bot is a member of (per-guild applies), a small PNG (< 10 MB), and a non-image text file.

| # | Action | Expected |
|---|---|---|
| 11.1 | `!presence watching the test server` | Activity changes immediately; confirmation. |
| 11.2 | `!presence` / `!presence nonsense text` / `!presence clear` | Show works; invalid input errors in-channel; clear removes. |
| 11.3 | `!bio line one` (with a line break) | Bio applies with the break preserved, visible in **both** servers. |
| 11.4 | `!bio` / `!bio clear` | Show; clear removes everywhere. |
| 11.5 | `!avatar` with the PNG attached, and with a direct image URL | Picture changes in both servers and DMs (global avatar). |
| 11.6 | `!avatar` with the text file, or `!avatar not-a-url` | In-channel rejection; nothing changes. |
| 11.7 | `!avatar reset` / `!banner clear` | Custom images removed; avatar falls back to the application default. |
| 11.8 | Second account runs `!presence playing chess`; same commands in a DM | Denial (tier 4); DMs work (no guild state). |
| 11.9 | Configure `BotConfiguration:Presence` (+ optionally `Bio`), restart | The configured state applies at Ready; a pre-restart runtime change is gone (not persisted). |
| 11.10 | Invalid `BotConfiguration:Presence:Type`, restart | Startup fails fast naming the key. |

## 12. Slash commands

Set `SLASH_COMMANDS_ENABLED=true`, restart; the log line "Registered N application commands with Discord." confirms. Global commands can take up to an hour to appear (re-login refreshes).

| # | Action | Expected |
|---|---|---|
| 12.1 | Type `/` | Exactly the administrative/configuration, user-scoped, searchquotes, version, and help commands — no quote or response commands. |
| 12.2 | `/remindme` with `10m water the plants` | Reply identical to the text surface, visible **only to the invoker** (ephemeral). |
| 12.3 | `/userpermission` picking a user + tier 1 (as guild owner) | Same effect as the text command. |
| 12.4 | Second account runs `/backup` | Ephemeral denial only; no public reply. |
| 12.5 | `/backup` twice inside the cooldown window | Ephemeral throttle reply; the shared cooldown applies on the text surface afterwards too. |
| 12.6 | `/remindme 10m test`, then `!remindme 10m test` | The text invocation is throttled — one cooldown key across surfaces. |
| 12.7 | `/help` as bot owner, click **Next ▶** | Page 1 with `page 1 of N` header and buttons; the *same* message updates in place to page 2. |
| 12.8 | `!help` (text surface), click its buttons | Same paging on a plain text message, regardless of `SlashCommands:Enabled`. |
| 12.9 | Spam the pagination buttons | Ephemeral throttle reply; the message stays unchanged. |
| 12.10 | `/userpermission` in a DM; `/reminders` in a DM | Guild-state decline; `/reminders` works. |
| 12.11 | `/restorebackup <name>` without confirm | The destructive-restore warning, same as text. |
| 12.12 | Set `SlashCommands__Enabled: false`, restart | Nothing registers; stale `/` entries persist until the next enabled boot's bulk overwrite (re-enable once before production to clear). Text surface unaffected. |
| 12.13 | `/searchquotes <text>` in a server with matching quotes | Same ranked results as the text surface, visible **only to the invoker**; the previous/next buttons page through the matches. |
| 12.14 | `/version` | Same version string as `!version`, visible only to the invoker (ephemeral). |

## 13. MCP endpoint

Set `MCP_ENABLED=true` + `MCP_API_KEY=<key>`, restart. Startup log: "MCP endpoint listening at http://0.0.0.0:2525/mcp"; reachable only from the deployment host. Any MCP client works; raw HTTP is a POST with `Authorization: Bearer <key>`, `Accept: application/json, text/event-stream`, JSON-RPC body (`initialize`, then `tools/call`).

| # | Action | Expected |
|---|---|---|
| 13.1 | Enabled but **no** key | "MCP endpoint disabled: … no Mcp:ApiKey"; nothing listens; `!health` agrees. |
| 13.2 | `MCP_ENABLED=false` | "MCP endpoint disabled: Mcp:Enabled is false". |
| 13.3 | POST without / with a wrong `Authorization` | 401 both times. |
| 13.4 | `bot_status` / `list_guilds` / `list_channels` / `list_commands` | Version, uptime, gateway, guild count, presence, bio; the test server; its channels (id, name, type); every command with tier/description/usage. |
| 13.5 | `get_recent_messages` (limit 5) / `send_message` | The 5 newest messages; the message appears in the channel. |
| 13.6 | `run_command` with `hello` | Reply returned as the tool result; **nothing** posted to any channel. |
| 13.7 | `run_command` with a tier-4 line on a fresh guild | "You are not permitted…" — the default agent tier is the lowest. |
| 13.8 | `!agenttier` (guild owner), then `!agenttier 4`, retry the tier-4 line | Shows tier 0 default; after raising, the command executes. |
| 13.9 | `!agenttier 1`, retry a tier-2+ line | Denied again — the per-guild tier governs the agent. |
| 13.10 | `run_command` with `agenttier 4` | "The operator agent cannot change its own tier in this server". |
| 13.11 | `!agenttier` from a non-owner account; `!agenttier` in a DM | Permission denial; DM decline. `!agenttier clear` resets to the default. |
| 13.12 | `!health` | The MCP line shows the endpoint state. |
| 13.13 | `/agenttier` (slash on) | Same show/set/clear, reply visible only to the invoker. |

## 14. User bot bans

Needs a second (non-owner) test account; run as the guild owner unless noted.

| # | Action | Expected |
|---|---|---|
| 14.1 | `!banuser <@second>` | "Banned user … permanent"; `!banlist` shows it. |
| 14.2 | Second account runs `!hello`, `!gibberish`, a custom-response name | **No reply of any kind** — not even "unavailable". |
| 14.3 | `!banuser <@second> 5m` (update) | "Updated the bot ban…"; `!banlist` shows expiry and remaining time. |
| 14.4 | Wait past the expiry, second account runs `!hello` | Executes — the ban lifted itself; `!banlist` no longer shows the user. |
| 14.5 | Banned user sends a DM `!remindme 10m test` | Scheduled — bans never block DMs. |
| 14.6 | `!unbanuser <@second>` twice; `!banuser <@guild-owner>`; `!banuser <@self>` | Unban then "not banned"; owner/self bans rejected. |
| 14.7 | Tier-0 account runs `!banuser`; tier-1 account bans another tier-1 | Permission denial; escalation-guard denial ("only users below your own tier"). |
| 14.8 | `!banuser <@second> because spam`; `!banuser notanid` | In-channel errors; nothing stored. |
| 14.9 | The ban commands in a DM | "only available in servers". |
| 14.10 | `!help` as a moderator; `/banuser` with duration `10m` (slash on) | Ban commands under the **Moderation** group; the slash form works, reply ephemeral. |

## 15. Quote of the day

Needs a test server with at least one quote and a channel the bot can post in; run as the guild owner.

| # | Action | Expected |
|---|---|---|
| 15.1 | `!quoteoftheday` | "off in this server — set a channel with !quoteoftheday channel <#channel>". |
| 15.2 | `!quoteoftheday channel <#target>` | Activated; first publication at the next schedule boundary. |
| 15.3 | `!quoteoftheday 3m 2m` | Confirmation naming the UTC time; the channel receives "quote of the day: #N: <text>" within ~3 min, then a second publication 2 min later — no burst. |
| 15.4 | `!quoteoftheday` | Reports channel, interval, next due time (UTC), template. |
| 15.5 | `!quoteoftheday template best of the server: {quote}` | Saved; the next publication uses the new wording. |
| 15.6 | `!quoteoftheday 5h 30s` (below the 1-minute floor); `!quoteoftheday banana 1d`, `5h`, `5h 90s`, `template no placeholder` | In-channel errors naming each problem; nothing stored. |
| 15.7 | `!quoteoftheday 5h 1d` | First publication at exactly that UTC time, then daily. |
| 15.8 | Delete the target channel (or revoke Send Messages), wait past a boundary, restore it | The boundary is skipped, the feature stays on; the next publication arrives at the next boundary. |
| 15.9 | `!quoteoftheday channel clear`; `!quoteoftheday` in a DM | Off; DM decline. |
| 15.10 | `/quoteoftheday` (slash on) | Same set as text; reply visible only to the invoker. |

## 16. LLM integration — phase 1 (presence & bio updates)

Opt-in (design §3.12): set `LLM_API_KEY=<Mistral API key>` and the job flags in `.env`, restart. Fast cadence: `LLM_PRESENCE_UPDATES_ENABLED=true`, `LLM_PRESENCE_INTERVAL_MINUTES=1`, `LLM_BIO_UPDATES_ENABLED=true`, `LLM_BIO_INTERVAL_MINUTES=1`. The startup log distinguishes the states: "LLM features are inactive…", "LLM features are active but no update job is enabled (manual !llm update still works).", "Presence update job started (interval N minutes)." / "Bio update job started (interval N minutes).".

| # | Action | Expected |
|---|---|---|
| 16.1 | No `LLM_API_KEY`: `!llm status`, `!health` | Status: "LLM: inactive (no Llm:ApiKey configured, or the daily budget is 0)"; the `!health` LLM line says the same; presence/bio never change. |
| 16.2 | Key set, both jobs off: restart, `!llm status` | "LLM: active"; model `mistral-small-latest` (configuration layer); kill switch off; `Requests today: 0 / budget 500`; presence unchanged. |
| 16.3 | Jobs on at 1 min: wait ~2 ticks, then `!presence` and the profile in **both** test servers | Activity changes to a new single line ≤ 128 chars in the persona voice; the bio changes too (≤ 190 chars, line breaks preserved); `!llm status` lists `presence`/`bio` with rising request and token counts. |
| 16.4 | `!llm off`, wait a tick, `!llm status` | Confirmation; kill switch ON (all calls blocked); counters stop rising; presence/bio unchanged. `!llm on` re-enables. |
| 16.5 | `!llm off`, then `docker compose restart bot`, `!llm status` | The kill switch is in-memory — after the restart it is off again (the configured state is restored). |
| 16.6 | `!llm status` / `!llm off` in a DM; from a second account; `/llm status`, `/model`, `/persona` (slash on) | DM works (bot-owner tier for `llm`); second account denied (`llm` is tier 4, `model`/`persona` tier 2); the slash replies are ephemeral with the same content as text. |
| 16.7 | `!llm banana` | "Unknown subcommand 'banana'" + usage. |
| 16.8 | `LLM_DAILY_REQUEST_BUDGET=2`, restart, wait past a tick | Status: "Requests today: 2 / budget 2 — EXHAUSTED for the rest of the UTC day"; later ticks skip (logged, nothing changes); the watchdog alerts budget exhaustion (owner DM + notification channel, §7 mechanics); `!health` shows "(exhausted)". |
| 16.9 | Wrong `LLM_API_KEY` with jobs on, wait ~3 ticks | Every tick logs a failure; presence/bio unchanged; the bot stays healthy; `!llm status` counts consecutive failures; at 3 the watchdog alerts. Restore the key → recovery announced once, counter resets. |
| 16.10 | `!settings llmchat on` in the test server, `LLM_CHAT_CONTEXT_MAX_MESSAGES=5`, `LLM_CHAT_CONTEXT_MAX_CHARACTERS=500`; post a few distinctive messages in that server, run `!llm update` there; also run `!llm update` in DM and in a non-opted-in server, and let a scheduled tick fire | Guild run in the opted-in server and scheduled ticks: the new presence/bio stays generic — no quoted chat, no user or channel names; `docker compose logs bot` contains none of the posted content (untrusted input is never logged). DM run and non-opted-in server: no chat is read at all. |
| 16.11 | `!model` / `!model list` / `!model <a listed name>` / `!model` / `!model clear` | Shows the effective model + layer; `list` returns the provider's models; setting one persists as the runtime override (visible as the override layer in `!llm status` from the next call); `clear` reverts to `Llm:Model`/the shipped default. |
| 16.12 | `!model not-a-real-model`; `!model` from a second (tier-0) account | "Unknown model…"; permission denied (admin tier). |
| 16.13 | `!persona` / `!persona You are a cheerful hamster.` / `!persona` / `!persona clear`; a >2000-char persona | Shows the shipped default; after setting, the runtime-override layer; `clear` reverts; over-limit rejected naming the 2000-char limit. |
| 16.14 | `!settings persona <text>`, `!settings persona`, `!settings persona clear` in the test server; same in a DM | Guild override set/shown/cleared (persisted per guild); the DM declines (guild-only). |
| 16.15 | `!help` as bot owner | The **LLM** category group lists `llm`, `model`, `persona`. |
| 16.16 | Jobs **off**, `!llm update` | Both surfaces update immediately (reply "Presence update applied." + "Bio update applied."); `!llm status` request count rises by 2 — the manual trigger works regardless of the job flags. | 
| 16.17 | `!llm update presence` / `!llm update bio`; `!llm update banana` | Only the named surface updates (1 request); the bad surface gets the usage error and no call is made. |
| 16.18 | `!llm off`, then `!llm update`; `!llm on`, then `!llm update` | Blocked reply ("kill switch … nothing changed"), no model call, nothing applied; after `on`, the update applies again. |
| 16.19 | `LLM_PRESENCE_INTERVAL_MINUTES=5`, `LLM_BIO_INTERVAL_MINUTES=1`, both jobs on, restart, wait ~3 min | The bio changes at its own ~1 min cadence while the presence only changes on its ~5 min cadence (watch both servers' profiles + `!llm status` feature counters). |
