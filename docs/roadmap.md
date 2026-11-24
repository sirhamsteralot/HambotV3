# Roadmap

Outstanding work for HambotV2 — nothing here is historical. Each task is fully specified in [`software-requirements.md`](software-requirements.md) and designed in [`software-design.md`](software-design.md) before implementation starts. When a task is signed off, remove it here and fold the as-built behavior into those two documents (removing any **(planned)** markers).

Each task carries: **Problem** (what's missing today), **Steps** (ordered checklist, each leaving the build green), and **Acceptance criteria** (what "done" means).

## Phase #2: LLM Integration

- [ ] **LLM conversation participation (Phase B) — specified, ready to implement (FR-170–FR-182 in §2.26 of the requirements; design §3.21; NFR-34)**

    **Problem**: phase 1 only generates presence text; the bot never talks in chat. Phase B lets it occasionally join the conversation in opted-in guilds.

    **Locked decisions**: Phase B only — memory & personality (Phase C) stay a separate future task below · builds directly on the §3.12 toolkit (the §3.20 registry pipeline stays **(planned)** until memory needs it) · triggers = keyword (bot `@` mention **or** its name fuzzy-matched — the bot's own username/display name/nickname in that guild, resolved live, no config key / reply-to-bot / per-guild keyword) + periodic (job on the shared `PeriodicJobHost`), probabilistic and scaled by channel activity — a trigger never guarantees a message · per-guild opt-in `!llmreply` + the shared `!llmchannels` allowlist governs reading and posting · minimal context now (triggering message + small same-channel window; attention-span history comes with memory) · per-guild daily reply cap `!llmreplycap` (`0` = unlimited) on top of the global gate budget · output never contains mentions (`AllowedMentions.None`) · separate top-level commands (`!llmreply`, `!llmreplycap`, `!llmkeywords`), and `!llmchat` is renamed to `!llmcontext` (FR-182) · presence/bio loops keep their dedicated loop (`!llminterval` stays; bot-chosen cadence is a future idea — see task 2).

    **Steps**:

    1. Rename `!llmchat` → `!llmcontext` across code, tests, README, manual-test-plan (FR-182).
    2. Migration `AddLlmParticipation`: `GuildLlmKeyword` table, `GuildSetting.LlmReplyEnabled` / `LlmReplyDailyCap` columns (§3.21).
    3. `ChatParticipationService` (hosted) + trigger/bounds/probability logic per §3.21: own `Attach()` on `MessageReceived` for activity counters + the message trigger (`@`/fuzzy name mention, reply-to-bot, keyword), periodic job on `PeriodicJobHost`, injectable random, per-guild semaphore; run path gate → `LlmPromptBuilder` → `ILlmClient` → `LlmOutputValidator` (new channel-message surface ≤ 2000) → send; `!llmstatus` gains the `chat` feature row.
    4. Commands `llmreply` / `llmreplycap` / `llmkeywords` in `Commands/LlmCommands/`, registered and slash-eligible (FR-170/174/179).
    5. Tests: `TestChatParticipation.cs` per NFR-34 (fake client, `FakeTimeProvider`, InMemory, injected random).
    6. Docs as-built at sign-off: drop **(planned)** in §3.21 / §2.26 / NFR-34, README command table + slash list, manual-test-plan §16 additions; build warning-free, tests green.

    **Acceptance criteria**: per FR-170–FR-182 / NFR-34 — opted-in guilds get occasional in-persona replies bounded by the cadence/cap/gate rules with no mentions and untrusted content never stored, logged, or executed; non-opted-in guilds see nothing; failures never crash the bot; build warning-free and tests green.

- [ ] **Phase B2 — adaptive reply decision ("when should I talk") — decisions locked; FRs/design to be authored when scheduled (reserve §2.27 in the requirements and §3.22 in the design; next free FR-183)**

    **Problem**: Phase B's reply decision is a fixed heuristic roll (FR-175–FR-177). The locked upgrade makes the bot decide more deliberately — attention, schedule, the model's own SKIP judgment, feedback — on top of Phase B's containment model, which does not change.

    **Locked decisions** (from review; Phase B's FR-170–FR-182 / §3.21 stay exactly as specced — B2 builds on top):

    - **Gate chain**: heuristic roll first (budget guard, with the B2 signals below added to its inputs) → then a single LLM call per passed candidate that returns `SKIP` or the reply text — the model itself decides whether to join. That decision call sees a wider context window than Phase B's minimal window (separate `Llm:Chat:*` config key).
    - **New roll inputs**: a per-channel in-memory attention score (exponential decay, half-life `Llm:Chat:AttentionHalfLifeMinutes` default 60; raised by candidate triggers and by positive reactions 👍/❤️; scales p up to a cap); a schedule multiplier (weekday × UTC-hour table, e.g. Mon–Fri 17–23 h ×1.5; multiplier 0 = quiet hours; default flat ×1, all UTC); extra signals: message-length bounds and a thread-vs-main-channel base — on top of Phase B's activity tiers / burst / question / author-rotation.
    - **Reaction feedback**: reactions adjust p directly via the attention score AND are summarized into the review below; requires adding the `MessageReactions` intent explicitly (minimal-intent rule, §3.1); default emoji 👍/❤️ positive, 👎 negative.
    - **Periodic LLM review job**: own interval (default 24 h) as a job on the shared `PeriodicJobHost`; reviews metadata only (counts, outcomes, tokens — never message content, FR-159); proposes changes to the global `Llm:Chat:*` knobs and posts the proposal to the notification channel (§3.13); the bot owner approves or rejects with a 👍/👎 reaction there; nothing auto-applies.
    - **SKIP accounting**: a SKIP is not a reply — the per-guild daily cap (FR-179) counts posted replies only; SKIPs count as requests under the global daily budget and the `chat` usage row, and are not watchdog failures.
    - **Per-guild knob mirror**: new `GuildLlmChatSetting (GuildId, Key, Value)` key-value table mirroring each `Llm:Chat:*` knob by name, managed by one command `!llmchatset <knob> <value|reset>` plus a show-all; resolution: guild override → global config → shipped default.

    **Acceptance criteria**: none yet — authored together with the FRs when scheduled.

- [ ] **LLM memory & personality (Phase C) — future (not yet specified)**

    **Problem**: the end goal beyond Phase B/B2 is a bot that participates with memories and a personality.

    **Plan notes** (pipeline shape designed in §3.20 with FR-157–FR-160 / NFR-33 in §2.25 of the requirements; the phase-C *feature* FRs are authored when scheduled. Runs are started by registered `IContextTrigger`s and fed by registered `IContextCollector`s — `ChatContextCollector` for history, a `MemoryCollector` for phase C — on top of the §3.12 foundation toolkit: `LlmGate`/`ILlmUsageTracker` guardrails, `LlmPromptBuilder` with its trust rules; plus the presence service (§3.10) and the MCP endpoint (§3.11)):

    - per-guild tables (conversation summaries, remembered facts), an LLM summarization job, prompts assembled from the persona seed + memories + recent messages; admin commands to inspect/clear a guild's memory.
    - **Trust boundaries**: channel content is **untrusted input** — data, never instructions; it must never trigger commands, tools, database writes, or MCP calls, or change the persona/permissions/config. Secrets are never prompt content; instructions embedded in channel text are ignored. Phase C must spec the one deliberate exception to FR-159's "never stored": raw channel content is never persisted, but LLM-derived summaries may be — what a summary may retain about users is a spec decision.
    - **Attention-span history** (feeds Phase C): previous message history from the time the bot was "attentive" to a channel — the attention *score* itself is Phase B2 (§3.22 reserved).
    - **Built-in LLM vs. MCP**: complementary. Built-in features use the raw Mistral key; the operator agent can already drive the bot via MCP (`send_message`, `run_command`). Decide per feature which path fits.

    **Acceptance criteria**: none yet — specifying this task is the first step once scheduled.

- [ ] **Run the LLM presence/bio loops on the shared scheduling engine — evaluate, then migrate or document the decision (extends §3.8 / §3.12; FRs to be authored when scheduled, numbered after §2.27 / Phase B2)**

    **Decision so far**: the dedicated loop stays for now and `!llminterval` keeps working as the runtime interval override; the bot deciding its own update cadence ("when it feels like updating") is a future idea, not part of this task. Settle (a) migrate onto `PeriodicJobHost` vs. (b) document keeping the dedicated loop explicitly in §3.12.

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

- [ ] **FuelUsageMonitor — Torque real-time upload ingestion and a per-trip Discord summary (receive path specified and designed, ready to implement: §2.29 FR-201–FR-211 + NFR-36, design §3.24; the statistics half still needs FRs from FR-213 and the §3.24 extension — FR-183–191 belong to Phase B2; the wire format is captured in [`torque-log-upload-server-wire-format-and-endpoint-requirements.md`](torque-log-upload-server-wire-format-and-endpoint-requirements.md))**

    **Split**: the **receive path is specified and designed, ready to implement** — §2.29 FR-201–FR-212 + NFR-36 of the requirements, design §3.24: opt-in endpoint on the existing Kestrel host, query-string-only contract with the exact `OK!` ack, bearer/path-secret auth, bounds, an uploaded body accepted and discarded (FR-212 — the transport works today, nothing is parsed), sample parsing, queue handoff, `!health` counters, privacy and failure rules. The second half (accumulation, `FuelUsageTrip`, statistics, commands, channel post) is still to be authored, and design §3.24 already marks the consumer loop as its single replacement point. Steps 1–2 below are therefore no longer spec work; the rest of the checklist follows once the receive path lands.

    **Problem**: nothing in the bot measures what members' vehicles burn. Members drive with Torque Pro/Lite (Android + Bluetooth OBD-II) and its **realtime web upload** pushes live telemetry to a URL of their choosing — but that traffic is neither a file nor a body: it is one query-string sample per logging interval, and an endpoint that parses a body silently receives nothing. The bot has no inbound HTTP surface except the operator-agent MCP endpoint (§3.11) and no trip statistics, so the datastream has nowhere to go.

    **Requested shape**: an upload endpoint Torque can point at; the incoming sample stream is accumulated into trips and each finished trip is summarised — litres used, kilometres driven, fuel usage total and average — into that guild's configured channel. No LLM, no external lookups: the arithmetic is local and the only network traffic is the Discord post.

    **Wire contract** (as captured, to be absorbed into §3.24):

    | Property | Detail |
    |---|---|
    | Method | `GET` **and** `POST` on the same URL — the body is always empty, everything rides in the query string |
    | Success | HTTP 200 with body exactly `OK!`; the app's *Test settings* button checks the same, and anything else is treated as failure (possible retry/alert) |
    | Cadence | One request per *Web Logging Interval* (a few seconds; ~1 s in older versions), so work must happen off the request thread |
    | Sample keys | `session` (drive start, epoch ms) + `id` (32 hex) identify the drive, `time` (epoch ms) orders samples within it, `v` is the format version |
    | Identity | `eml` — the *User Email Address* field verbatim, the only user/vehicle discriminator; `profileName` is a usable label, other `profile*`/`userUnit*`/`defaultUnit*` keys are ignorable |
    | PIDs | Open-ended `k*` keys, and the key set can differ request to request: `kc` RPM, `kd` speed, `k2f` fuel level, `kff1001` GPS speed, `kff1005`/`kff1006` latitude/longitude, `kff1201` trip distance; the fuel-rate key depends on the member's custom PID |
    | Caveats | Duplicate keys possible (pick one policy), `0.0` is a real reading, absent PIDs simply do not appear, spaces in `eml` break the app's encoding |
    | Auth | Torque Pro ≥ 1.12.46 sends `Authorization: Bearer <token>` when enabled, over https only |
    | Privacy | Every request carries live GPS coordinates — TLS plus a token, or a VPN, and no coordinate persistence |

    **Steps** (initial plan; full FR set and design authored as the first step once scheduled):

    1. Spec first: move the captured contract into §3.24 and settle which PID carries fuel: litres come from integrating a flow-rate PID, or from a fuel-level drop, and the key is member-configured (`kff…`), so the parser keys off a configurable PID name rather than a hardcoded one. Verify against one captured drive from the requester's own app.
    2. Ingestion endpoint: a minimal-API route on the Kestrel host the MCP endpoint already runs in (`FuelUsageMonitor:ListenPath`, default `/fuel/upload`) that accepts both methods, parses the query string only, validates the token, answers `200 OK!` immediately, and hands the parsed sample to a `Channel<T>` read by a background consumer — a per-sample database write on the request thread would fall behind a 1 s uploader.
    3. Trip accumulation: an in-memory accumulator per (token → guild, `session`, `id`), fed from the channel:

        | Statistic | Derivation |
        |---|---|
        | Litres used | ∫ fuel-rate PID dt over the sample timestamps, or fuel-level drop × tank capacity; guarded against resets and gaps |
        | Kilometres driven | Trapezoidal ∫ speed dt (`kd`, falling back to `kff1001`), dropping gaps > `FuelUsageMonitor:MaxSampleGapSeconds` (120) so a paused app is not smeared across the trip |
        | Average fuel usage | litres ÷ km × 100 (L/100 km), or MPG in the guild's unit system |
        | Fuel usage total | The finished trip's aggregate, as posted in the summary |

    4. Trip finalisation: a drive ends when its last sample is older than `FuelUsageMonitor:TripIdleMinutes` (default 10) or a new `session` arrives for the same token — swept by a job on the shared `Scheduling/PeriodicJobHost` (§3.8, the watchdog/quote-of-the-day pattern). The finished trip persists as a `FuelUsageTrip` row (guild, channel, token id, session, `id`, `eml`/`profileName` label, start/end UTC, km, litres, average usage, sample count) via migration `AddFuelUsageTrips` and posts exactly one summary embed, `AllowedMentions.None`, through the existing notification path (§3.13).
    5. Duplicates and retries: `(session, id, time)` is idempotent — a resend after a reconnect updates the accumulator instead of double-counting, and a replayed completed session never double-posts.
    6. Commands in a new `FuelUsageMonitor/` + `Commands/FuelUsageMonitorCommands/` set: `!fuelchannel <#channel|off>` (tier 2, guild-only), `!fueltoken [rotate]` (tier 3, shows the upload URL/token and rotates it), `!fuel [days]` (recent trips plus rolling totals). Help text, category, slash-eligible per §3.15.
    7. Auth, bounds, trust: one bearer token per guild/vehicle pair, hashed at rest — the query string is not where a secret belongs, because reverse proxies log URLs; a plain-HTTP or VPN-only deployment may instead use an unguessable path segment. `eml` is validated against the values registered for that token and is never an instruction; `MaxUploadBytes` (small, 64 KB) and `MaxUploadsPerMinute` (30) per token; an unknown token is refused, while a malformed *sample* from a known token is counted, logged without content, and still answered `OK!` so the app does not retry-storm — a bad sample never kills the listener.
    8. Privacy: raw samples and coordinates stay in memory only; the finished trip stores distance and volume, never a position or a route. TLS terminates at the reverse proxy, which is why the bearer header (not a URL secret) is the default.
    9. Config and deployment: `FuelUsageMonitor:Enabled` (false), `FuelUsageMonitor:ListenPath` (`/fuel/upload`), `FuelUsageMonitor:FuelRatePid` (empty = derive litres from fuel level), `FuelUsageMonitor:FuelCapacityLitres`, `FuelUsageMonitor:MaxSampleGapSeconds` (120), `FuelUsageMonitor:TripIdleMinutes` (10), `FuelUsageMonitor:MaxUploadsPerMinute` (30), `FuelUsageMonitor:UnitSystem` (`metric`), optional `FuelUsageMonitor:DefaultChannelId`; compose `FUELUSAGEMONITOR_*` → `FuelUsageMonitor__*` beside the MCP port settings, plus the published port and https note in §4/§5. Tokens are runtime state (`!fueltoken`), never configuration.
    10. Tests: sample→accumulator arithmetic against a hand-computed drive; gap handling, fuel resets, duplicate `(session, time)`, unknown `k*` keys, `0.0` readings; GET/POST parity and the exact `OK!` body with a fast return; finalisation on idle and on session rollover; token auth and rate bounds; post path over a fake sender; InMemory database, `FakeTimeProvider`.
    11. Docs at sign-off: §2.29 and §3.24 as-built (drop **(planned)**), §4 config table, §5 published port, README command table, manual-test-plan section; state in §6 that this inbound endpoint is machine-to-machine ingestion of a member's own telemetry, not the out-of-scope web interface or admin panel.

    **To settle when the FRs are authored**:

    - **Which PID carries fuel** — the open-ended `k*` set and member-defined custom PIDs decide whether litres are integrated from flow or derived from a fuel-level drop; `FuelUsageMonitor:FuelRatePid` + `FuelUsageMonitor:FuelCapacityLitres` are the placeholders for that choice.
    - **Sample retention**: in-memory accumulator (no GPS ever stored, a mid-drive restart loses that trip) vs the contract's narrow `(session, id, time, pid, value)` rows with a retention window — privacy favours memory, replay/debugging favours rows.
    - **Live trips**: realtime mode permits an in-progress "current trip" view or per-trip progress ping; keep the feature to completed trips unless the requester wants live telemetry.
    - **Multiple vehicles per guild**: one token per vehicle (cleaner separation, more tokens) versus one token per guild with `eml`/`profileName` as the label.
    - **The finished-log path**: file uploads now arrive and are discarded (FR-212), which proves the transport without deciding anything; whether such a CSV is ever parsed into a trip stays open — the realtime stream is the only data source the statistics half needs.

    **Acceptance criteria**: none yet — specifying this task is the first step once scheduled; after that: a member's drive streams samples to the guild's URL and the bot answers `200`/`OK!` fast enough for a 1 s cadence; on trip end exactly one embed lands in the configured channel with litres used, kilometres driven and average fuel usage computed from the log's own speed and fuel data; metric and imperial uploads both compute correctly; an unknown token is refused, and malformed samples, duplicate resends, unknown PIDs and mid-drive gaps neither crash the listener nor double-post; no GPS coordinate or raw sample is persisted; a restart mid-drive costs at most the in-flight trip; build warning-free and tests green.

- [ ] **Further extra features — placeholder (not yet specified)**

    Candidates get their own entry here (Problem / Steps / Acceptance criteria) once proposed and agreed, then are fully specified in the requirements and design docs before implementation.

