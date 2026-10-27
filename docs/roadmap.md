# Roadmap

Planned work for HambotV2. Each task is fully specified in [`software-requirements.md`](software-requirements.md) and designed in [`software-design.md`](software-design.md) before implementation starts.

## How to use this file

For each task:

- **Problem**: what is wrong or missing today.
- **Steps**: the ordered implementation checklist — take them top to bottom; each step leaves the solution building and tests green.
- **Acceptance criteria**: what defines "done" (mirrored from the requirements).

When a task is complete and signed off, remove it from this file and fold the resulting behavior into the design and requirements documents so they keep describing the system as it actually is — in particular, remove the **(planned)** markers from the feature's design section. This file tracks only outstanding work; nothing is kept here for historical context.

## Phase #2: LLM Integration

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

## Phase #3: Extra features

- [ ] **Automated hall of fame — reaction tracking and a scheduled digest of a guild's best messages (FRs and design section to be authored when this task is scheduled)**

    **Problem**: the best moments of a server — the messages the community reacted to hardest or found funniest — scroll away and are forgotten. There is no record of them and no way to resurface them: quotes capture only what someone manually saves, and nothing tracks what the community itself actually upvoted with reactions.

    **Steps** (initial plan; the full FR set and design section are authored as the first step once this task is scheduled — after Phase #2, whose LLM foundations could later enrich the digest):

    1. Add reaction tracking: request the `MessageReactions` gateway intent explicitly (keeping the minimal-intent rule in design §3.1), and record reaction adds/removes in a new per-guild table (message id, channel id, author, content snapshot, reacting user, reaction emoji, timestamps). Bots reacting and self-reactions do not score.
    2. Add the scoring model: a message's score is the count of distinct non-bot, non-author reactions (per emoji), within a rolling window configurable via `HallOfFame:WindowDays` (default 7); a message qualifies only above `HallOfFame:MinScore` (default 3).
    3. Add the scheduled digest: a hosted loop (the `Scheduling/` engine pattern) posts, per opted-in guild on the configured cadence (`HallOfFame:DigestCron`, default weekly), the top `HallOfFame:DigestSize` (default 5) qualifying messages into the guild's configured channel — original message link plus content snapshot, ranked by score, deduplicated so a message is never featured twice.
    4. Add per-guild opt-in and configuration via a new `GuildSetting` flag plus admin commands: `!hallconfigure` (set/clear the target channel, show current settings) and `!halloffame` (show the current top of the board on demand, same ranking, no digest side effects); server-scoped, tier-appropriate defaults, help text, in-channel validation errors.
    5. Wire configuration defaults into `appsettings.json`, document in design §4, and cover with unit tests (NFR to be assigned): score computation (distinct users, bot/self exclusion, window and floor boundaries), digest selection and dedup, per-guild opt-in gating, command behavior and validation — all without a live Discord connection (mocked client, in-memory database).
    6. Decide and document the interaction with quotes when specifying the FRs (e.g. a one-command promote-to-quote from the digest) so the two features complement rather than duplicate each other.

    **Acceptance criteria**: none yet — specifying this task (FRs, design section, test NFR) is the first step once it is scheduled; after that, the criteria mirror the requirement set: with a guild opted in, the digest appears on schedule with the right messages ranked by community reactions, a message is never repeated, non-opted-in guilds see nothing, and the build stays warning-free with tests green.

- [ ] **Further extra features — placeholder (not yet specified)**

    **Problem**: community-facing extras beyond the hall of fame are expected over time, but none is specified yet.

    **Plan notes**: candidates get their own entry here (Problem / Steps / Acceptance criteria) once proposed and agreed; each is fully specified in [`software-requirements.md`](software-requirements.md) and designed in [`software-design.md`](software-design.md) before implementation starts, per this file's rules.
