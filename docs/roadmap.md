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

- [ ] **Run the LLM presence/bio loops on the shared scheduling engine — evaluate, then migrate or document the decision (extends §3.8 / §3.12; FRs to be authored when scheduled, next free FR-168)**

    **Problem**: `PresenceUpdateService` hand-rolls one `while` + `Task.Delay` loop per surface, while `Scheduling/` already hosts a generic delivery engine (§3.8) with wake-up signals (`IScheduledDeliverySignal`), a bounded fallback poll against clock drift, `TimeProvider`-based sleeping, and maintenance-mode pausing. Two loop implementations to maintain; the LLM loops currently get none of the engine's drift-safe polling or maintenance pause. (§3.20 already notes phase 1 keeps its own loop and that migrating is optional.)

    **Fit analysis to settle first**: the engine is DB-backed due-message delivery (`IScheduledMessageSource`/`IScheduledMessageSender` per kind, retry-then-mark-failed semantics), while the LLM loops are in-memory interval jobs whose failure semantics are "keep current state, count toward the watchdog, retry next tick" — the same reason quote-of-the-day deliberately uses a dedicated loop (§3.17). So either (a) add a sibling `IScheduledJob` abstraction to `Scheduling/` (named job, interval, run callback) and host the presence/bio jobs on it, reusing the signal for wake-ups, or (b) decide the semantics differ enough that the dedicated loop is correct and document that decision in §3.12. Decide explicitly; do not half-merge the two shapes.

    **Steps** (if (a)):

    1. Spec first: author the FRs in §2.14 and the design delta in §3.8/§3.12 before any code; mark the behavior deltas (maintenance pause, failure handling) explicitly.
    2. `Scheduling/`: add `IScheduledJob` (name, current interval, run callback) plus a hosted job runner that sleeps on the injected `TimeProvider` with the bounded fallback poll; reuse `IScheduledDeliverySignal` so `!llm interval` wakes the loops and re-arms them with the new interval.
    3. Move the presence/bio loops onto the jobs, keeping `PresenceUpdateService`'s run lock so manual `!llm update` and scheduled runs never overlap; keep gate/validator behavior identical (kill switch, budget, per-surface validation, watchdog failure counting).
    4. Decide and document maintenance-mode behavior for LLM ticks (the engine pauses passes during a restore; the LLM loops currently do not) — either adopt the pause or spec the difference.
    5. Tests: port the TestLlm cadence cases to the job host over `FakeTimeProvider` — per-surface cadence, wake-up + re-arm on interval change, serialization with manual updates, failure keeps state and counts.
    6. Docs: design §3.8/§3.12 as-built notes; README and manual-test-plan §16 only if externally visible behavior changes.

    **Acceptance criteria**: one loop implementation for scheduled work (or a documented, specced decision to keep the dedicated loop); identical externally visible behavior — same cadences and `!llm interval` runtime overrides, same kill-switch/budget gating, manual and scheduled runs serialized, failed ticks keep current state and count toward the watchdog; build warning-free and tests green.

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

## Phase #4: Consolidation & technical debt

Refactoring items — no externally visible behavior change unless a step says so. FRs/design deltas authored when scheduled.

- [ ] **Unify the periodic hosted loops (watchdog §3.13, quote-of-the-day §3.17, LLM §3.12) onto one timer abstraction — cross-reference the phase-#2 scheduling-engine item**

    **Problem**: besides the reminder engine in `Scheduling/`, three hosted services each hand-roll their own sleep/poll loop: `WatchdogService` (`Task.Delay(CheckIntervalMinutes)`), `QuoteOfTheDayPublicationService` (10 s poll), `PresenceUpdateService` (per-surface interval loops — covered by the phase-#2 item). Each re-implements cancellation, `TimeProvider` sleeping, and — inconsistently — maintenance-mode pausing and fallback polls.

    **Steps**:

    1. Decide the shared shape together with the phase-#2 item: either the `Scheduling/` job runner (if adopted there) or a small `PeriodicJobHost` base (interval supplier + `TimeProvider` + stop token + optional maintenance pause + bounded fallback poll).
    2. Migrate `WatchdogService` and `QuoteOfTheDayPublicationService` onto it (the LLM loops follow the phase-#2 item); keep each loop's exact semantics: the watchdog's alert/re-alert cadence and recovery announcements, the quote-of-the-day's "a failed post skips its boundary, no retry, re-baseline on schedule change" rule, maintenance pausing where it exists today.
    3. `DiscordClient`'s 5-minute keep-alive log loop is gateway logging, not a scheduled job — leave it unless the abstraction fits trivially.
    4. Tests: port the watchdog and quote-of-the-day suites to the shared host over `FakeTimeProvider` (cadence, pause/resume, cancellation); no behavior change expected.
    5. Docs: design §3.8/§3.13/§3.17 as-built notes if the shape changes.

    **Acceptance criteria**: one loop implementation for all periodic hosted work; identical externally visible behavior (alerts, publication boundaries, maintenance pause); build warning-free and tests green.

- [ ] **Shared settings stores: one `GuildSetting` upsert path and one layered-override resolver (persona/model/BotSetting)**

    **Problem**: the per-guild row upsert exists twice with subtly different lookups — `SettingsCommand.GetOrAddGuildSetting` (`FirstOrDefault`) vs `QuoteOfTheDayCommand.LoadOrCreateSettingAsync` (`FindAsync`) — and `BotSettingOverride` (added with the LLM commands) is the `BotSetting` analog. The persona (`LlmPersona.ResolveWithLayer`/`ResolveGlobalWithLayer`) and model (`LlmRuntimeSettings.ResolveModelWithLayer`) resolvers repeat the same override → config → shipped-default-with-layer-label pattern.

    **Steps**:

    1. Add one `GuildSettingStore.GetOrAdd` (pick one lookup semantic, `FindAsync` on the key) in `Connections/` next to `BotSettingOverride`; migrate both command call sites.
    2. Extract a generic layered resolver for `BotSetting`-backed overrides (runtime override key → config value → default, returning the layer label); migrate `LlmPersona` and `LlmRuntimeSettings` onto it.
    3. Tests: existing suites cover the behavior — they must pass unchanged; add a boundary test only where the two old upsert paths differed.
    4. Docs: design §3.4/§3.12 note the shared helpers.

    **Acceptance criteria**: single upsert path per table with one lookup semantic, single layered-resolver implementation, no behavior change (persona/model resolution chains and layer labels identical), build warning-free and tests green.

- [ ] **One HTTP content downloader with a shared `HttpClient` (merge `AttachmentContentDownloader` + `ImageContentDownloader`)**

    **Problem**: `Commands/AttachmentContentDownloader.cs` (quote import) and `Commands/ImageContentDownloader.cs` (avatar/banner) each declare their own `static readonly HttpClient` and their own "GET url → capped bytes" path; only the image one adds validation (real image, within Discord's limits).

    **Steps**:

    1. One `IHttpContentDownloader` over a single shared `HttpClient` (one static instance or `IHttpClientFactory`), returning capped bytes/string; validation becomes an optional post-step (image sniff + size/type limits for avatar/banner, none for quote import).
    2. Migrate `!importquotes` and `!avatar`/`!banner`; keep error semantics (in-channel error, nothing stored/set on failure) and limits identical.
    3. Tests: existing suites over the fake downloaders must pass unchanged; the production classes stay thin glue.
    4. Docs: design §3.6/§3.10 note the single downloader.

    **Acceptance criteria**: one downloader implementation and one `HttpClient` for all URL content fetches; identical validation, caps, and failure behavior; build warning-free and tests green.

