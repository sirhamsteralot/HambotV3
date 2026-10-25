# Roadmap

Planned work for HambotV2. Each task is fully specified in [`software-requirements.md`](software-requirements.md) and designed in [`software-design.md`](software-design.md) before implementation starts.

## How to use this file

For each task:

- **Problem**: what is wrong or missing today.
- **Steps**: the ordered implementation checklist — take them top to bottom; each step leaves the solution building and tests green.
- **Acceptance criteria**: what defines "done" (mirrored from the requirements).

Tick a task off (`- [x]`) when complete and fold the resulting behavior into the design and requirements documents so they keep describing the system as it actually is — in particular, remove the **(planned)** markers from the feature's design section.

## Status

Everything specified so far is implemented and in production:

- **Quotes** — quote storage, recall, search, and legacy import (FR-42–FR-50, FR-72–FR-75; design §3.6).
- **Custom command responses** (FR-51–FR-60; design §3.7).
- **Reminders** (FR-61–FR-71; design §3.8).
- **Jenkins auto-deploy** (NFR-6/NFR-8; design §5.1) — version tags (`v<major.minor.patch>`) that build and test cleanly deploy automatically; ordinary pushes (including `master`) are built and tested only.
- **Version reporting** (FR-76–FR-77 / NFR-17 / C-5; design §3.9) — the running bot reports a real release version.
- **Backup & stability improvements** — database backups (FR-81–FR-85, FR-96–FR-98, FR-100–FR-101 / NFR-19, NFR-22; design §5.2) and watchdog, health reporting & operator notifications (FR-105–FR-106, FR-117–FR-118 / NFR-23; design §3.13), shipped in `v1.1.2`; the as-built behavior is folded into the design and requirements documents. One operator action remains open: the restore drill (NFR-19) — run [`docs/manual-test-plan.md`](manual-test-plan.md) §3 and §8 against a test deployment once and record the outcome.

The remaining backlog is specified ahead of implementation and organised into phases:

- **Phase #1: Rich Presence, Recurring reminders, Slash Commands** — Discord presence & profile identity (FR-78–FR-80, FR-113, FR-115–FR-116 / NFR-18; design §3.10), recurring reminders (FR-107–FR-109 / NFR-24; design §3.14), and slash commands (FR-110–FR-112 / NFR-26; design §3.15). The slash-command handler's virtual invocation builds the shared run-through-`CommandHandler` mechanism specified in design §3.11 — extract it as a reusable component here, so Phase #2's `run_command` tool consumes it rather than re-inventing it.
- **Phase #2: MCP** — the MCP server for Odysseus (FR-86–FR-91 / NFR-20; design §3.11).
- **Phase #3: LLM Integration** — LLM-driven presence and bio updates (FR-92–FR-95, FR-99, FR-102–FR-104, FR-114 / NFR-21, NFR-25; design §3.12), then LLM conversation participation & memory (a deliberately unspecced placeholder; design it after everything above is in production). The LLM-related watchdog conditions (design §3.13) activate when this phase lands.

## Phase #1: Rich Presence, Recurring reminders, Slash Commands

- [ ] **Discord presence & profile identity — activity, bio, avatar, banner (FR-78–FR-80, FR-113, FR-115–FR-116 / NFR-18; design §3.10)**

    **Problem**: the bot has no Discord presence/activity — it always shows the plain "online" state — and its profile is bare: no "About Me", no custom profile picture, no banner. There is no way for server members to see what the bot is doing or what it is, and no foundation for the LLM to drive its identity later.

    **Steps**:

    1. Add `Connections/PresenceService.cs` — singleton `IPresenceService` over the shared gateway client: `ApplyAsync`/`ClearAsync`/`Current` for the activity, plus bio/avatar/banner apply and clear alongside, single-flight serialization, deferred apply until the gateway is ready, apply failures logged never thrown.
    2. Add the optional `BotConfiguration:Presence` config section (`Type` + `Text`) and `BotConfiguration:Bio` with startup validation naming the offending key; apply on gateway Ready. No startup configuration for images.
    3. **Spike (design §3.10)**: verify how bots set bio/avatar/banner with Discord.Net 3.20.2 — the API has allowed bot accounts to set these since September 2025 via the per-guild modify-current-member route, so the service applies them in every guild the bot is a member of; the avatar additionally uses the global user route; use Discord.Net's native support where present, else a small REST call on the shared client's REST layer.
    4. Add the `!presence` command (`SystemCommands`): show / `<type> <text...>` set / `clear`; default tier 4 (bot owner), DM-usable, help text, in-channel validation errors, not persisted across restarts.
    5. Add the `!bio` command mirroring it: show / `<text...>` set (≤ 190 chars, line breaks allowed) / `clear`; same tier-4 default, DM-usable, help text; absent config never wipes an existing bio at startup.
    6. Add the `!avatar` and `!banner` commands: no args → current image URL; exactly one image attachment or a single image URL → download (attachment path reuses the `IAttachmentContentDownloader` pattern, URL path the shared `HttpClient`), validate as an image within Discord's limits, apply; `!avatar reset` / `!banner clear` remove the custom images. Tier 4, DM-usable, help text, failures in-channel.
    7. Unit tests per NFR-18 (mocked client, fake image downloader): config validation (presence and bio, both boundaries), command behavior, tier gates, DMs, serialization, apply-failure path, per-guild applies, not-persisted semantics, avatar/banner attachment and URL paths with rejection cases.

    **Acceptance criteria**: FR-78–FR-80, FR-113, and FR-115–FR-116 hold; the build stays warning-free and tests green; design §3.10 loses its **(planned)** marker and reflects the as-built behavior (including the spike's outcome on the member route).

- [ ] **Recurring reminders — `!remindme every <duration> <message...>` (FR-107–FR-109 / NFR-24; design §3.14)**

    **Problem**: reminders are one-shot only — `!remindme 1d water the plants` fires once and is gone, so anything habitual (medication, birthdays, weekly cleanups) must be re-created after every delivery.

    **Steps**:

    1. Add the nullable `RecurrenceInterval` column to `Reminder` (migration; existing rows stay one-shot).
    2. Extend `remindme` to recognize the leading `every` token: parse the interval with the existing duration parser, enforce the configurable minimum (`Reminders:MinRecurrenceMinutes`, default 10), first due = now + interval, reply states the recurrence.
    3. Extend the delivery source: on delivering a recurring row, re-schedule it from the actual delivery time instead of deleting (no burst of catch-up after downtime); the bounded-retry failure logic stops a recurring reminder that keeps failing, like a one-shot.
    4. Extend `!reminders` to mark recurring entries with their interval; `!cancelreminder` needs no change beyond working on recurring rows.
    5. Enforce the separate per-user cap (`Reminders:MaxRecurring`, default 5) that never consumes one-shot slots (FR-70).
    6. Unit tests per NFR-24 (§3.8's style): `every` parsing and floor, creation reply, multi-cycle redelivery with schedule-from-delivery, numbering stability, list marking, cancellation, caps, one-shot independence.

    **Acceptance criteria**: FR-107–FR-109 hold; a recurring reminder fires once per interval, survives restarts, shifts (never bursts) across downtime, is visible and cancellable like any reminder; the cap and floor are enforced in-channel; tests green and warning-free; design §3.14 loses its **(planned)** marker.

- [ ] **Slash commands — the command surface as Discord application commands (FR-110–FR-112 / NFR-26; design §3.15)**

    **Problem**: the command surface is text-only. Users must know the identifier and argument order, there are no typed Discord-native inputs or autocomplete, and Discord's built-in command discovery (`/`) shows nothing for the bot.

    **Steps**:

    1. Add `SlashCommands:Enabled` (default false) and a registration pass: build one application-command definition per registered command from the same registry — name/description from `ICommand`, typed options from optional per-command application metadata, `arguments` string fallback for commands without it — pushed as a bulk global overwrite after the gateway connects (idempotent).
    2. Add the interaction handler: convert the invocation to its equivalent text command line and execute through the real `CommandHandler` via virtual invocation (the §3.11 `run_command` mechanism) — same permission tiers, same cooldown keys (slash and text share the throttle per user per command), same audit events, same error semantics, DM scope for DM interactions.
    3. Add the reply plumbing: acknowledge the interaction immediately, post the command's normal output publicly in the channel, send permission/cooldown/validation denials as ephemeral responses.
    4. Add per-command application metadata to the commands where typed options beat a raw `arguments` string (e.g. `removequote <number>`, `userpermission <user> <tier>`), starting with the highest-traffic commands.
    5. Unit tests per NFR-26: interaction→command-line mapping (typed options + fallback), shared pipeline semantics, denial ephemerality and public output, DM scope, registration contract.
    6. Verify in production behind the flag, then flip the default to true.

    **Acceptance criteria**: FR-110–FR-112 hold; with the flag on, every registered command appears in Discord's `/` picker with sensible options and executes exactly as its text twin (same tiers, throttles, audit, errors); denials are ephemeral, output is public and identical to the text surface; the text surface is unchanged; tests green and warning-free; design §3.15 loses its **(planned)** marker.

## Phase #2: MCP

- [ ] **MCP server — Odysseus hook-in (FR-86–FR-91 / NFR-20; design §3.11)**

    **Problem**: there is no way for the operator's agent (Odysseus) to interact with the bot programmatically — no status introspection, no message reading/sending, no command execution.

    **Steps**:

    1. Add the official `ModelContextProtocol` C# SDK and host the streamable-HTTP endpoint via Kestrel in the worker; config `Mcp:Enabled` (default false) / `Mcp:ListenUri` / `Mcp:ApiKey`; bearer-key auth in front of everything; startup log line naming the state and URI.
    2. Implement the status & identity tools: `bot_status` (version, uptime, guild count, presence and bio), `list_guilds`, `set_presence` and `set_bio` (via `IPresenceService`, design §3.10).
    3. Implement the messaging tools: `get_recent_messages` (Discord REST, capped at 50) and `send_message` (plain text ≤ 2000 chars).
    4. Implement `run_command` — virtual invocation through the real `CommandHandler` as tier 4: reply captured and returned as the tool result, never posted to a real channel; cooldowns, custom responses, audit events reused unchanged.
    5. Wire compose (loopback-only publish) and `.env.example`; document the endpoint and tool surface.
    6. Unit tests per NFR-20: bearer rejection, each tool over fakes, `run_command` capture/cooldown/audit semantics.

    **Acceptance criteria**: FR-86–FR-91 hold; Odysseus connects with the API key and completes a status → read → `run_command` round trip (and is rejected without the key); tests green and warning-free; design §3.11 loses its **(planned)** marker.

## Phase #3: LLM Integration

- [ ] **LLM integration, phase 1 — automatic presence and bio updates, including the bot's persona (FR-92–FR-95, FR-99, FR-102–FR-104, FR-114 / NFR-21, NFR-25; design §3.12)**

    **Problem**: the bot's presence and bio are static. The plan is for the bot to use an LLM (a raw Mistral API key) to keep its identity surfaces fresh — phase 1 of the broader LLM integration (conversation participation and memory are later phases, deliberately out of this scope).

    **Steps**:

    1. Add `Llm/ILlmClient.cs` (chat-completion abstraction) and `Llm/MistralLmClient.cs` (Mistral API, key from `Llm:ApiKey`, model from `Llm:Model`, default `mistral-small-latest`); no key = every LLM feature inactive.
    2. Add `Llm/PresenceUpdateService.cs` — opt-in hosted loop (`Llm:PresenceUpdates:Enabled`, interval `IntervalMinutes`, default 60): build the prompt from `Llm:Persona` + bot name/version + current date/time + current presence, validate the output (single line ≤ 128 chars), apply via `IPresenceService`.
    3. **Author the bot's persona (FR-99)**: draft the default persona seed — the hamster character's voice, tone, and humor — as a documented, configuration-overridable default, then tune it against real presence outputs once the job runs. The persona is part of the feature, not an afterthought.
    4. Failure path: request error or invalid output → keep the current presence (and bio), log, retry next tick (FR-94).
    5. Add bio updates (FR-114): `Llm:BioUpdates:Enabled` (default false) makes the same tick generate and apply a bio — non-empty, ≤ 190 chars, line breaks allowed, from the same persona — with identical validation, failure handling, and budget accounting.
    6. Add the cost & usage guardrails (FR-102–FR-104): per-feature/per-day usage accounting in the LLM client, the `Llm:DailyRequestBudget` hard stop (suspend for the rest of the UTC day, like any other failure — skip, change nothing, log), and the `!llm` runtime kill switch (`status` / `off` / `on`, tier 4, DM-usable; a restart restores the configured state).
    7. Wire configuration defaults into `appsettings.json`, secrets per NFR-3, and document in design §4.
    8. Unit tests per NFR-21 and NFR-25 (fake `ILlmClient`, `FakeTimeProvider`): prompt building, output validation at both boundaries (128-char presence, 190-char bio), scheduling, failure path, inactive-without-key; usage accounting, budget suspension and UTC-rollover resumption, kill-switch states and replies.

    **Acceptance criteria**: FR-92–FR-95, FR-99, FR-102–FR-104, and FR-114 hold; with a key and the flags on, the presence and bio change on schedule in the bot's own voice; without them, nothing changes; the budget can be observed via `!llm status` and enforced (a spent budget suspends the features until UTC rollover), and `!llm off` stops all API calls until re-enabled or restarted; tests green and warning-free; design §3.12 loses its **(planned)** marker.

- [ ] **LLM conversation participation & memory — future (not yet specified)**

    **Problem**: phase 1 only generates presence text. The end goal is a bot that occasionally checks conversation, replies and participates like a real user, with memories and a personality.

    **Plan notes** (to be turned into FRs + a design section when this task is scheduled — deliberately after the tasks above, which provide its foundations: the LLM client and guardrails, the presence service, and the MCP endpoint):

    - **Phase B — conversation participation**: per-guild opt-in (a `GuildSetting` flag managed by an admin command), a bounded cadence (e.g. at most one unsolicited reply per N messages / M minutes), reading recent channel history via the Discord REST API (the same surface as the MCP `get_recent_messages` tool), and replying as the bot within Discord's message limits. Failure and rate-limit handling like FR-94: log, skip, never crash.
    - **Phase C — memory & personality**: new per-guild tables (conversation summaries; remembered facts about users), an LLM summarization job over recent history, and prompt assembly from the persona seed (FR-99) + memories + recent messages. Admin commands to inspect and clear a guild's memory.
    - **Trust boundaries & prompt injection** (hardening that must be part of phase B's spec, written down now so it cannot be retrofitted away): conversation content is **untrusted input**. Text read from channels — including text that ends up in memories and summaries — is data, never instructions: it must never trigger commands, tools, database writes, or MCP calls, and it must never change the bot's persona, permission behavior, or configuration. The LLM's output in phase B is restricted to message text (no tool use, no function calling); secrets (tokens, keys, connection strings) are never eligible prompt content; and phase B refuses or ignores attempts to make the bot reveal its instructions or act on embedded directives. The per-guild opt-in and the cadence bounds are the primary containment — the bot participates only where an admin turned it on, at a bounded rate.
    - **Odysseus vs. raw key**: the two integration paths are complementary, not exclusive. The bot's built-in features (the LLM and slash/backlog tasks above) use the raw Mistral API key directly; Odysseus can additionally drive the bot through the MCP endpoint (`send_message`, `run_command`), which already exists by then. Phase B/C should decide per feature which path fits — built-in autonomy via the LLM client, agent-driven behavior via MCP.

    **Acceptance criteria**: none yet — specifying this task (FRs, design section, test NFR) is the first step once it is scheduled.
