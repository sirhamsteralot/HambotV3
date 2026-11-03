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

- [ ] **Run the LLM presence/bio loops on the shared scheduling engine — evaluate, then migrate or document the decision (extends §3.8 / §3.12; FRs to be authored when scheduled, next free FR-170)**

    **Problem**: `PresenceUpdateService` hand-rolls one `while` + `Task.Delay` loop per surface, while `Scheduling/` already hosts the shared `PeriodicJobHost` for fixed-interval jobs (§3.8 — the watchdog and quote-of-the-day run on it) with a bounded fallback poll against clock drift, `TimeProvider`-based sleeping, and maintenance-mode pausing, plus the generic due-time delivery engine with wake-up signals (`IScheduledDeliverySignal`). Two loop shapes to maintain; the LLM loops currently get none of the host's drift-safe polling or maintenance pause. (§3.20 already notes §3.12 keeps its own loop and that migrating is optional.)

    **Fit analysis to settle first**: the delivery engine is DB-backed due-message delivery (`IScheduledMessageSource`/`IScheduledMessageSender` per kind, retry-then-mark-failed semantics), while the LLM loops are in-memory interval jobs whose failure semantics are "keep current state, count toward the watchdog, retry next tick" — the same reason quote-of-the-day and the watchdog deliberately stay off the due-time engine and run on the shared `PeriodicJobHost` (§3.8, §3.17). So either (a) host the presence/bio jobs on the `PeriodicJobHost`, extended with the signal for wake-ups, or (b) decide the semantics differ enough that the dedicated loop is correct and document that decision in §3.12. Decide explicitly; do not half-merge the two shapes.

    **Steps** (if (a)):

    1. Spec first: author the FRs in §2.14 and the design delta in §3.8/§3.12 before any code; mark the behavior deltas (maintenance pause, failure handling) explicitly.
    2. `Scheduling/`: extend `PeriodicJobHost` with an optional `IScheduledDeliverySignal` wake-up so `!llminterval` wakes the loops and re-arms them with the new interval.
    3. Move the presence/bio loops onto the jobs, keeping `PresenceUpdateService`'s run lock so manual `!llmupdate` and scheduled runs never overlap; keep gate/validator behavior identical (kill switch, budget, per-surface validation, watchdog failure counting).
    4. Decide and document maintenance-mode behavior for LLM ticks (the engine pauses passes during a restore; the LLM loops currently do not) — either adopt the pause or spec the difference.
    5. Tests: port the TestLlm cadence cases to the job host over `FakeTimeProvider` — per-surface cadence, wake-up + re-arm on interval change, serialization with manual updates, failure keeps state and counts.
    6. Docs: design §3.8/§3.12 as-built notes; README and manual-test-plan §16 only if externally visible behavior changes.

    **Acceptance criteria**: one loop implementation for scheduled work (or a documented, specced decision to keep the dedicated loop); identical externally visible behavior — same cadences and `!llminterval` runtime overrides, same kill-switch/budget gating, manual and scheduled runs serialized, failed ticks keep current state and count toward the watchdog; build warning-free and tests green.

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

- [ ] **Further extra features — placeholder (not yet specified)**

    Candidates get their own entry here (Problem / Steps / Acceptance criteria) once proposed and agreed, then are fully specified in the requirements and design docs before implementation.

