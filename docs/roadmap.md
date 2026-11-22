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

- [ ] **Embed-fix link reposts — detect X/Twitter and Reddit links and repost a fixed-embed link, per-guild opt-in — requirements authored, design to write (FR-192–FR-199 in §2.28 of the requirements, NFR-35; design §3.23 still to be authored; next free FR-200)**

    **Problem**: X/Twitter and Reddit links unfurl as a bare link or a broken preview in Discord — X serves no usable unfurl metadata at all, and Reddit's own unfurl loses video audio and extra images. Members hand-edit the domain before sending today. Nothing rewrites those links automatically.

    **Domain research** (mirror projects verified; every mirror accepts the origin's path shape, so a rewrite is a **host swap only** — path, query, and fragment are preserved):

    | Origin hosts (case-insensitive, any subdomain of the listed ones) | Rewritten host | Mirror project |
    |---|---|---|
    | `x.com`, `twitter.com` (`www`/`mobile`/`m`) | `fxtwitter.com` — `fixupx.com` is the same service | FixTweet / FxEmbed |
    | same | `vxtwitter.com` — `fixvx.com` is the same service | vxTwitter |
    | `reddit.com` (`www`/`old`/`new`/`np`/`m`), `redd.it` | `redditez.com` (default) | redditez |
    | same | `rxddit.com` | fxreddit |
    | same | `vxreddit.com` | vxReddit |

    **Locked decisions**: the origin→mirror mapping is **data resolved per rewrite in two layers, never a hardcoded per-site branch** — the shipped defaults in the table above, overridden at runtime by a row persisted in the existing `BotSetting` key-value store (§3.12) — because these are community-run services that get rate-limited or blocked at the source, repointing a link must never need a deploy · the runtime layer is **`!linkfixmap`** (tier 4 bot owner, **global**, DM-usable, slash-eligible): no args shows the effective table with overridden rows marked `runtime` (the `!llmstatus` convention); `!linkfixmap <origin-host> <mirror-host>` sets/overrides a row — and can add an origin the shipped table lacks, so a new social site needs no code change; `!linkfixmap clear <origin-host>` drops the override back to the shipped default (or removes an override-only row); both arguments validated as bare lowercase hostnames (`^[a-z0-9.-]+$`, no scheme/path/port), and an origin may not itself be a known mirror host (so a mirror link is never re-rewritten), table capped at `LinkFix:MaxMappings` (default 50), and a change applies from the next message — no restart, no cache to invalidate · **no config layer for the hosts**: with the runtime command a `LinkFix:RedditHost` key would add nothing but an env key with a dot in it, so the shipped defaults live in one mapping table in `LinkFix/` and the only `LinkFix:*` config keys are the bounds · the mapping is global by design — no per-guild override (a guild that dislikes the mirror asks the bot owner); the guild knob stays the on/off toggle · no automatic mirror failover (that would mean fetching/validating the mirror, which reintroduces a network dependency; settle a static fallback list when specifying) · **pure textual rewrite, never a fetch of the target URL** — no SSRF surface, no rate-limit exposure, no LLM/gate cost, nothing stored or logged from message content · the original message is never edited or deleted (a bot cannot edit a member's message): the bot posts one follow-up carrying the fixed links with `AllowedMentions.None`, and the reference (reply-to-original for context vs. plain message) must be chosen so it cannot suppress the unfurl · per-guild opt-in `!linkfix [on|off]` (tier 2, guild-only, slash-eligible — the only guild-level knob here) persisting `GuildSetting.LinkFixEnabled`, **default false** — no global switch, so the feature runs only in guilds that opted in, and never in DMs · skips = bot/webhook authors, its own messages, command invocations (identifier prefix), messages with no matching host, links already on a known mirror host (no double rewrite), above `LinkFix:MaxLinksPerMessage` (default 5) distinct links and per-channel cooldown to keep hot channels from being spammed · nothing persisted beyond the toggle and the mapping overrides: no link, URL, or message text in the database, logs, or health output; a failed repost is logged and swallowed (FR-94 shape) and maintenance mode pauses the listener.

    **Steps** (each leaves the build green; FRs and design authored as step 1):

    1. Design first (the FRs are already authored in §2.28, FR-192–FR-199): write §3.23 — the rewrite table, two-layer mapping resolution, skip rules, bounds, repost shape, both command contracts — plus the `LinkFix:*` bound keys in §4 and the component/DI entry points in §2.1/§3.4.
    2. Migration `AddLinkFix`: `GuildSetting.LinkFixEnabled` (bool, default false, §3.4 column list). No new table or column for the mapping — overrides are `BotSetting` rows (`LinkFixMap:<origin-host>` → mirror host), reusing the existing `BotSettingOverride` get/set/clear helpers; that helper is generic but sits in `Llm/` today, so promote it to a neutral home (`Connections/`) first rather than duplicating it in `LinkFix/` (resolution is two layers here: runtime override → shipped default in `LinkFix/`, so `LayeredBotSettingResolver` is not needed).
    3. `LinkFix/` pure rewriter and shipped defaults (no Discord types, unit-testable): the origin→mirror table (origins matched case-insensitively against the host of each URL, subdomain-tolerant), message text + resolved mapping in → distinct rewritten URLs out, path/query/fragment preserved, trailing punctuation stripped, mirror hosts and unresolvable short paths (`redd.it/<id>`, `x.com/i/...`) handled per the spec decision.
    4. Hosted `EmbedLinkRepostService` with its own `MessageReceived` `Attach()` (the `ButtonPaginationService`/§3.21 pattern — `DiscordClient` untouched, no new intents: `GuildMessages | MessageContent` already cover it): opt-in check, skip rules, mapping resolved once per message (an in-memory snapshot swapped on write, so `!linkfixmap` needs no invalidation), in-memory per-channel cooldown, one `SendMessageAsync` with `AllowedMentions.None`, maintenance-mode pause, failures logged and swallowed.
    5. Commands in `Commands/LinkFixCommands/`, registered via a new `RegisterLinkFixCommands` in the existing registration chain: `linkfix [on|off]` (tier 2, guild-only, slash-eligible) — show state when omitted, persist through `GuildSettingStore`; `linkfixmap [<origin-host> <mirror-host> | clear <origin-host>]` (tier 4, DM-usable, slash-eligible) — show the effective table when omitted, validate both hostnames, enforce the `MaxMappings` cap, persist through the `BotSetting` path; both reuse `CommandArguments`.
    6. Config plumbing for the bounds only: `LinkFix:MaxLinksPerMessage` (5), `LinkFix:ChannelCooldownSeconds`, `LinkFix:MaxMappings` (50) in `appsettings.json` with defaults on absent/invalid, mapped from compose `.env` (`LINKFIX_*` → `LinkFix__*`) and documented in `.env.example`.
    7. Tests `TestLinkFix.cs`: rewrite table (every origin host and subdomain, case-insensitivity, query/fragment/trailing punctuation), mapping layering (shipped default, override wins, `clear` restores the default, override-only origin disappears on `clear`), `!linkfixmap` guards (hostname validation incl. scheme/path/port rejection, cap, tier-4-only, DM-usable, and that a change applies to the next rewrite with no restart), skip cases (bot/webhook/own message, command invocation, non-opted-in guild, DMs, already-mirrored host, over-limit, dedupe), cooldown boundary, `!linkfix` behavior, and that nothing is written to the database or logged as content.
    8. Docs once built: README command table + feature list (both commands), a manual-test-plan section, `docs/software-design.md` as-built (drop the reservation markers here).

    **Acceptance criteria**: none yet — specifying this task is the first step once scheduled; after that: an opted-in guild gets one follow-up per member message that carries fixed-embed links for its X/Twitter/Reddit URLs (original message untouched, no mentions), guilds that did not opt in see nothing, DMs and command invocations are never touched, the bot owner can repoint or add an origin→mirror row at runtime (`!linkfixmap`, effective from the next message, no restart and no deploy, surviving a restart), no link or message content is stored or logged, build warning-free and tests green.

- [ ] **Further extra features — placeholder (not yet specified)**

    Candidates get their own entry here (Problem / Steps / Acceptance criteria) once proposed and agreed, then are fully specified in the requirements and design docs before implementation.

