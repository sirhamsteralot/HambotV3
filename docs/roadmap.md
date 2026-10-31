# Roadmap

Outstanding work for HambotV2 — nothing here is historical. Each task is fully specified in [`software-requirements.md`](software-requirements.md) and designed in [`software-design.md`](software-design.md) before implementation starts. When a task is signed off, remove it here and fold the as-built behavior into those two documents (removing any **(planned)** markers).

Each task carries: **Problem** (what's missing today), **Steps** (ordered checklist, each leaving the build green), and **Acceptance criteria** (what "done" means).

## Phase #2: LLM Integration

- [ ] **LLM conversation participation & memory — future (not yet specified)**

    **Problem**: phase 1 only generates presence text. The end goal is a bot that occasionally participates in conversation with memories and a personality.

    **Plan notes** (pipeline shape designed in §3.20 with FR-157–FR-160 / NFR-33 in §2.25 of the requirements; the phase-B/C *feature* FRs are authored when scheduled. Runs are started by registered `IContextTrigger`s and fed by registered `IContextCollector`s — `ChatContextCollector` for history, a `MemoryCollector` for phase C — on top of the §3.12 foundation toolkit: `LlmGate`/`ILlmUsageTracker` guardrails, `LlmPromptBuilder` with its trust rules; plus the presence service (§3.10) and the MCP endpoint (§3.11)):

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

- [ ] **Absolute-time reminders — `!remindme at <datetime> <message...>` (FRs and design section to be authored when scheduled; extends §2.8/§2.16, design §3.8)**

    **Problem**: `!remindme` only accepts relative durations (FR-62). "Remind me at 18:00" or "at 2026-05-01 09:30" currently has to be computed by hand into `Nh`, which is error-prone around day boundaries, and there is no way to anchor a recurring reminder to a wall-clock time.

    **Steps** (initial plan; full FR set and design authored as the first step once scheduled):

    1. Spec the syntax: a leading `at` token on `!remindme` switches to absolute-time parsing (`remindme at <datetime> <message...>`); decide whether `remindme every at <datetime> <interval> <message...>` anchors a recurrence at a wall-clock time, and keep all times UTC-only (consistent with the quote-of-the-day all-UTC rule, FR-127) — no per-user time zones.
    2. Accepted datetime forms to spec: ISO-like (`yyyy-MM-dd`, `yyyy-MM-dd HH:mm`, optional seconds, optional `Z`), and the unambiguous `dd.mm.yyyy HH:mm` style; a bare time (`HH:mm`) means the next occurrence of that time UTC. Unparsable or past datetimes are in-channel errors, nothing stored (mirrors FR-63).
    3. Implementation: extend `RemindMeCommand`/`ReminderDurationParser` with the `at` branch (parsed due time instead of duration); the `Reminder` entity, delivery engine (FR-65), caps (FR-70/FR-109), listing, and cancellation stay unchanged — only how `DueAtUtc` is computed differs.
    4. Confirmation reply echoes the absolute due time in UTC (FR-64 shape); slash form (`/remindme`) parses the same free-form arguments (FR-111).
    5. Unit tests per the NFR-16/NFR-24 pattern (`FakeTimeProvider`, InMemory): parsing boundaries (each accepted form, past-time rejection, garbage), next-occurrence rollover for bare times, cap interaction, DM usability (FR-71), recurring anchor if specced.
    6. Document in design §3.8/§4 and the README command table once built.

    **Acceptance criteria**: none yet — specifying this task is the first step once scheduled; after that: `!remindme at <datetime> <message...>` stores and delivers at the exact UTC instant like any other reminder, invalid or past input is an in-channel error with nothing stored, the existing duration and `every` forms are unchanged, build warning-free and tests green.

- [ ] **Per-guild LLM chat-context channel allowlist — `!settings llmchannels [add|remove <#channel|id> | clear]` (FR-164–FR-166; extends §2.14 and design §3.12's chat context)**

    **Problem**: `!settings llmchat on` opts a whole server into LLM chat context — every readable text channel is read. There is no way to keep the feature on while excluding specific channels (secret, off-topic, or noisy ones).

    **Steps** (initial plan; the as-built design folds into §3.12 when scheduled):

    1. Requirements are authored with this entry (FR-164–FR-166 at the end of §2.14, NFR-21 coverage); when scheduled, add the as-built notes to design §3.12 (ChatContextCollector row, `settings` command row, §3.4 entity list) and mark the FRs implemented.
    2. Data: new entity `GuildLlmChannel` (key `(GuildId, ChannelId)`, cascade-deleted with the guild row) + migration (`dotnet ef migrations add AddGuildLlmChannels` from `HambotV2/`).
    3. Command: extend `SettingsCommand` with `llmchannels` — show / `add <#channel|id>` / `remove <#channel|id>` / `clear` — tier 2, guild-only, free-form slash; accept a raw channel id or a `<#id>` mention; reject channels outside this guild or non-text channels with an in-channel error.
    4. Read path: `PresenceUpdateService.ResolveRunScope` loads each opted-in guild's allowlist together with `LlmIncludeChat` and passes the per-guild allowlists to `ChatContextCollector.CollectAsync` (signature gains them; an empty list = all readable channels); unlisted and unreadable channels are skipped, never fail the run.
    5. Tests per the NFR-21 pattern (InMemory + fakes): allowlist filtering per guild, empty-means-all, a non-opted-in guild contributing nothing even with rows, add/remove/clear command behavior incl. invalid channel, scheduled vs guild-triggered scope.
    6. Docs when built: design §3.12/§3.5/§3.4, the README `!settings` row, and a manual-test-plan §16 case.

    **Acceptance criteria**: in an opted-in guild with an allowlist, `!llm update` run there and scheduled ticks read only the listed (and readable) channels; `clear` restores all-readable behavior; a non-opted-in guild still contributes no chat; nothing is stored or logged from unlisted channels; build warning-free and tests green.

- [ ] **Further extra features — placeholder (not yet specified)**

    Candidates get their own entry here (Problem / Steps / Acceptance criteria) once proposed and agreed, then are fully specified in the requirements and design docs before implementation.
