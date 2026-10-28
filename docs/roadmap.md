# Roadmap

Outstanding work for HambotV2 — nothing here is historical. Each task is fully specified in [`software-requirements.md`](software-requirements.md) and designed in [`software-design.md`](software-design.md) before implementation starts. When a task is signed off, remove it here and fold the as-built behavior into those two documents (removing any **(planned)** markers).

Each task carries: **Problem** (what's missing today), **Steps** (ordered checklist, each leaving the build green), and **Acceptance criteria** (what "done" means).

## Phase #2: LLM Integration

- [ ] **LLM integration, phase 1 — automatic presence and bio updates, including the bot's persona (FR-92–FR-95, FR-99, FR-102–FR-104, FR-114 / NFR-21, NFR-25; design §3.12)**

    **Problem**: the bot's presence and bio are static. Use an LLM (a raw Mistral API key) to keep its identity surfaces fresh. Conversation participation and memory are later phases, deliberately out of scope.

    **Steps**:

    1. `Llm/ILlmClient.cs` (chat-completion abstraction) + `Llm/MistralLmClient.cs` (Mistral API; key `Llm:ApiKey`, model `Llm:Model` default `mistral-small-latest`); no key = every LLM feature inactive.
    2. `Llm/PresenceUpdateService.cs` — opt-in hosted loop (`Llm:PresenceUpdates:Enabled`, interval default 60 min): prompt from `Llm:Persona` + bot name/version + date/time + current presence; validate (single line ≤ 128 chars); apply via `IPresenceService`.
    3. Author the default persona seed (FR-99) — the hamster character's voice — as a documented, configuration-overridable default, tuned against real outputs. Part of the feature, not an afterthought.
    4. Failure path (FR-94): error or invalid output → keep current presence/bio, log, retry next tick.
    5. Bio updates (FR-114): `Llm:BioUpdates:Enabled` makes the same tick generate and apply a bio (≤ 190 chars, line breaks allowed) with identical handling and budget accounting.
    6. Guardrails (FR-102–FR-104): per-feature/per-day usage accounting; `Llm:DailyRequestBudget` hard stop (like any other failure: skip, change nothing, log); `!llm status|off|on` kill switch (tier 4, DM-usable; restart restores the configured state).
    7. Wire config defaults into `appsettings.json`; secrets per NFR-3; document in design §4.
    8. Unit tests per NFR-21/NFR-25 (fake `ILlmClient`, `FakeTimeProvider`): prompts, both validation boundaries, scheduling, failure path, inactive-without-key, accounting, budget suspension/rollover, kill switch.

    **Acceptance criteria**: the FRs above hold; with a key and the flags on, presence and bio change on schedule in the bot's own voice; without them, nothing changes; the budget is observable via `!llm status` and enforced until UTC rollover; `!llm off` stops all calls until re-enabled or restarted; tests green and warning-free; design §3.12 loses its **(planned)** marker.

- [ ] **LLM conversation participation & memory — future (not yet specified)**

    **Problem**: phase 1 only generates presence text. The end goal is a bot that occasionally participates in conversation with memories and a personality.

    **Plan notes** (FRs + design section to be authored when scheduled; builds on the phase-1 client and guardrails, the presence service, and the MCP endpoint):

    - **Phase B — conversation participation**: per-guild opt-in (a `GuildSetting` flag), bounded cadence (≤ 1 unsolicited reply per N messages / M minutes), reading recent history via the Discord REST API, replying as the bot; failure handling like FR-94.
    - **Phase C — memory & personality**: per-guild tables (conversation summaries, remembered facts), an LLM summarization job, prompts assembled from the persona seed + memories + recent messages; admin commands to inspect/clear a guild's memory.
    - **Trust boundaries (must be part of phase B's spec)**: channel content is **untrusted input** — data, never instructions; it must never trigger commands, tools, database writes, or MCP calls, or change the persona/permissions/config. Phase-B output is restricted to message text (no tool use); secrets are never prompt content; instructions embedded in channel text are ignored. The per-guild opt-in and cadence bounds are the primary containment.
    - **Built-in LLM vs. MCP**: complementary. Built-in features use the raw Mistral key; the operator agent can already drive the bot via MCP (`send_message`, `run_command`). Decide per feature which path fits.

    **Acceptance criteria**: none yet — specifying this task is the first step once scheduled.

## Phase #3: Extra features

- [ ] **Automated hall of fame — reaction tracking and a scheduled digest of a guild's best messages (FRs and design section to be authored when scheduled)**

    **Problem**: a server's best messages — the ones the community reacted to hardest — scroll away and are forgotten; nothing tracks what the community itself upvoted.

    **Steps** (initial plan; full FR set and design authored as the first step once scheduled):

    1. Reaction tracking: request the `MessageReactions` intent explicitly (minimal-intent rule, design §3.1); record reaction adds in a per-guild table (message/channel/author/content snapshot, reacting user, emoji, timestamps). Bots and self-reactions don't score.
    2. Scoring: distinct non-bot, non-author reactions per emoji within a rolling window (`HallOfFame:WindowDays`, default 7); qualify above `HallOfFame:MinScore` (default 3).
    3. Scheduled digest (the `Scheduling/` engine pattern): per opted-in guild on `HallOfFame:DigestCron` (default weekly), post the top `HallOfFame:DigestSize` (default 5) qualifying messages — link plus snapshot, ranked, deduplicated.
    4. Per-guild opt-in and commands: `!hallconfigure` (set/clear channel, show settings) and `!halloffame` (show the current top on demand); server-scoped, help text, in-channel validation.
    5. Config defaults in `appsettings.json`, design §4 documentation, unit tests (mocked client, in-memory database): scoring boundaries, digest selection/dedup, opt-in gating, command behavior.
    6. Decide the interaction with quotes when specifying the FRs (e.g. a one-command promote-to-quote) so the features complement rather than duplicate.

    **Acceptance criteria**: none yet — specifying this task is the first step once scheduled; after that, the criteria mirror the requirement set: opted-in guilds get the digest on schedule with the right messages ranked by community reactions, never repeated, non-opted-in guilds see nothing, build warning-free and tests green.

- [ ] **Further extra features — placeholder (not yet specified)**

    Candidates get their own entry here (Problem / Steps / Acceptance criteria) once proposed and agreed, then are fully specified in the requirements and design docs before implementation.

## Phase #4: Operator tooling

- [ ] **MCP member lookup tools — look up one specific member (FR-139–FR-143; design §3.11)**

    **Problem**: the operator agent (FR-86–FR-91, FR-120) can act on a guild but cannot look anyone up: to investigate a specific member it has no way to resolve a name to a user id, see who they are on Discord, what standing they hold with the bot (tier, ban), what they said recently, or which quotes they added.

    **Steps**:

    1. `get_user_profile(guildId, userId)` (FR-139): member identity via one new `IMcpDiscordClient` member read (username/display name, nickname, roles, join date; graceful degradation when the member left) plus the bot's per-guild records — stored tier (default 0), active bot-ban record (expired ones lift on sight), quote count with the most recent quote, last-modified custom responses. Reminders stay private (FR-61).
    2. `search_users(guildId, query, limit ≤ 25)` (FR-143): the discovery step — Discord's REST member search over username/display name/nickname, resolving a name the agent encountered to a user id (a full member list is unnecessary; current members only).
    3. `get_user_quotes(guildId, userId)` (FR-140): the member's quotes, newest first, number-prefixed, capped at 50 with a "+N more" count.
    4. `get_user_messages(guildId, userId, limit)` (FR-141): the member's recent messages across the bot's readable guild channels, newest first, capped at 50, reusing the `get_recent_messages` REST path filtered by author.
    5. Wire the tool definitions into `McpToolEndpoints.cs`; keep them read-only (no writes, no pipeline, no posting — the operator-agent tier FR-120 does not gate them; they decline during maintenance mode); unit tests per NFR-20 (EF InMemory through `IDatabaseContext`, fake Discord REST).

    **Acceptance criteria**: FR-139–FR-143 hold — the tools resolve a name to a user id and return the member's Discord identity, bot standing, quotes, and recent messages within the bot's existing access, all read-only behind the endpoint's authentication and tracing; nothing is written, posted, or compiled; tests green and warning-free; §3.11's member-lookup block loses its **(planned)** marker.
