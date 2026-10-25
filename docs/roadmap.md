# Roadmap

Planned work for HambotV2. The three feature tasks are fully specified in [`software-requirements.md`](software-requirements.md) (quotes FR-42–FR-50 plus the later-added legacy import FR-72–FR-75, custom command responses FR-51–FR-60, reminders FR-61–FR-71) and designed in [`software-design.md`](software-design.md) §3.6–§3.8; task 4 is a CI/CD infrastructure task (Jenkins auto-deploy). This file sequences the work so the tasks can be taken one by one.

## How to use this file

For each task:

- **Problem**: what is wrong or missing today.
- **Steps**: the ordered implementation checklist — take them top to bottom; each step leaves the solution building and tests green.
- **Acceptance criteria**: what defines "done" (mirrored from the requirements).

Tick a task off (`- [x]`) when complete and fold the resulting behavior into the design and requirements documents so they keep describing the system as it actually is — in particular, remove the *(planned)* markers from the feature's design section.

## Implementation order

1. **Quotes** *(priority)* — self-contained: one entity + migration, three commands (plus the later-added legacy import command `!importquotes`, FR-72–FR-75, which arrived after task 1 was complete); no pipeline changes.
2. **Custom command responses** *(priority)* — the only task that touches `CommandHandler` (the dispatch fallback, FR-54); it goes second so the fallback lands on a stable pipeline.
3. **Reminders** — adds the first additional hosted service; independent of the other two, but goes last so the hosted-service change stays isolated.
4. **Jenkins auto-deploy** — pure infrastructure, independent of all feature tasks; do it whenever the server setup is ready. Deploying each feature task as it lands (instead of one big deploy after all three) exercises the pipeline early and keeps releases small.

Each task is independently shippable: it builds, tests, and deploys on its own (`dotnet build` / `dotnet test`, then the usual container build). The features share no state — quotes and responses are per-guild, reminders per-user — so there are no cross-feature dependencies beyond the shared command pipeline. Steps 1–3 of each task follow the same shape (entity & migration → commands → tests); the verification step (build + full test run) closes every task.

Shared conventions for all three tasks (from the design doc):

- New commands implement `ICommand` (lowercase `Name`, non-empty `Description`/`Usage`) and register in a new `RegisterQuoteCommands` / `RegisterResponseCommands` / `RegisterReminderCommands` wired into `RegisterAllCommands`.
- Persistence goes through `IDatabaseContext` (tests use the EF InMemory provider); one migration per feature, scaffolded with `dotnet ef migrations add <Name>` from `HambotV2/` and applied automatically at startup.
- Commands parse arguments through `CommandArguments` (or the shared `ReminderDurationParser` for durations), never hand-rolled `Split`/`TryParse` (NFR-13).
- No new gateway intents are required; DM behavior follows the DM scope (FR-25): guild-state commands decline with the established "only available in servers" message, user-scoped reminder commands stay fully usable.
- The build stays warning-free (NFR-7) and new types are `internal`.

## Tasks

### [x] 1. Quotes — quote storage and recall, per guild *(priority; FR-42–FR-50, NFR-14; design: §3.6)*

- **Problem**: HambotV2 cannot store or recall memorable quotes. The old bot's quote feature was global (shared across all guilds) with an index-based list, so removal shifted every later quote's number and text-based removal silently deleted all exact matches.
- **Steps**:
  1. **Entity & migration** — add the nested `Quote` class to `DatabaseContext` (key `GuildId + Number`; `Text`, `AddedByDiscordId`, `CreatedAt`, §3.4 table), expose it on `IDatabaseContext`, and scaffold the `AddQuotes` migration from the `HambotV2/` project directory.
  2. **Commands** — create `Commands/QuoteCommands/` with `quote` (random / by number / add-by-text), `searchquotes`, and `removequote` (default tier: moderator, FR-48); register them via a new `RegisterQuoteCommands` called from `RegisterAllCommands`. Sequential per-guild numbering (FR-43), 0-based like the old bot's index list: each new quote gets one greater than the guild's current maximum (`MAX(Number) + 1`, first quote = 0); removal never renumbers the remaining quotes. Follow the standard pipeline: `CommandArguments` parsing, help text, cooldowns, runtime permission adjustment (FR-49), DM declines (FR-50).
  3. **Tests** — add `BotTests/TestQuotes.cs` (EF InMemory + Moq) covering the NFR-14 scenarios.
  4. **Verify & document** — `dotnet build HambotV2.sln` and `dotnet test HambotV2.sln` (warning-free), remove the *(planned)* markers from design §3.6, and tick this task.
- **Acceptance criteria**:
  1. Quotes are scoped per guild and persisted in PostgreSQL: a quote records its text, the Discord ID of the user who added it, and the message timestamp. A quote added in one guild is never visible or removable in another guild.
  2. Each quote has a per-guild number assigned at creation as a sequential index, 0-based like the old bot's index list — one greater than the guild's current maximum number (first quote = 0), so the number increases by one with each quote added. Numbers of remaining quotes never change when another quote is removed.
  3. `!quote` with no arguments replies with a random quote from the invoking guild, prefixed with its number. A guild with no quotes gets a friendly "no quotes yet" message instead of an error.
  4. `!quote <number>` replies with that exact quote; a malformed number is treated as quote text to add (see criterion 6), and an unknown number produces an in-channel error message, never an exception or empty reply.
  5. `!searchquotes <text...>` replies with the guild's quotes whose text contains the search string (case-insensitive), number-prefixed and capped at 10 results; no matches produces a "no matching quotes" message. Empty search text is rejected with an in-channel error.
  6. `!quote <text...>` — any argument that is not a number — adds the text as a new quote (like the old bot: quoting is a single, immediate action) and replies with the assigned number. Text longer than 2000 characters is rejected with an in-channel error.
  7. `!removequote <number>` removes the quote and confirms in-channel; an unknown number produces an in-channel error. Its default required tier is **moderator**; `quote` (both fetching and adding) and `searchquotes` default to tier 0 (everyone).
  8. All quote commands are subject to the standard pipeline behavior: runtime permission adjustment via `!commandpermission`, per-tier cooldown throttling, help text via `!help`, and names enforced lowercase.
  9. Quote commands manage per-guild state and therefore decline in direct messages with the established "only available in servers" message (consistent with FR-25).
  10. Unit tests (MSTest + Moq, EF InMemory) cover: add/get/remove lifecycle with sequential numbering (including that removing the highest-numbered quote lets the next added quote reuse that top number), per-guild isolation, random fetch on a populated guild and the empty-guild message, fetch-by-number, add-by-text (including a numeric-looking argument that is out of range), text validation at the 2000-character boundary and the reply trimming of over-limit fetches and search results, search behavior and cap, permission gating, and DM declines.

- **Later addition** (not part of the original task): **`!importquotes`** (FR-72–FR-75, §2.9 / design §3.6 "Legacy import") — imports the old bot's `quoteSettings.json` attached to the invocation message into the invoking guild: bot-owner-only (default tier 4, effectively immutable at runtime), all-or-nothing validation (empty or over-2000-char entries abort with nothing stored), file-order append continuing the 0-based per-guild numbering, legacy dates preserved. Covered by `TestQuotes.cs` (import lifecycle, all failure paths, the tier-4 gate, DM decline; a fake `IAttachmentContentDownloader` stands in for the HTTP download).

### [x] 2. Custom command responses — user-defined text commands, per guild *(priority; FR-51–FR-60, NFR-15; design: §3.7)*

- **Problem**: Guild staff cannot define their own simple `!<name>` → text responses. The old bot's version was global, had no duplicate/edit handling, and any unmatched token silently fell through to custom responses without permission or cooldown checks.
- **Steps**:
  1. **Entity & migration** — add the nested `CustomResponse` class to `DatabaseContext` (key `GuildId + Name`; `Content`, `LastModifiedByDiscordId`, `CreatedAt`, `UpdatedAt`, §3.4 table), expose it on `IDatabaseContext`, scaffold the `AddCustomResponses` migration.
  2. **Dispatch fallback** — in `CommandHandler`, after a failed built-in lookup and before the "unavailable" reply: look up `(GuildId, token)` in `CustomResponse` (guild messages only — DMs keep the "unavailable" reply, FR-59) and run the hit through the same permission check (required tier from `CommandMetaData`, default 0) and cooldown (keyed on the response name) as a built-in; add the `(name, defaultTier)` entry point to `IPermissionsService` so both paths share tier semantics (§3.7).
  3. **Commands** — create `Commands/ResponseCommands/` with `addresponse` (create-or-update, moderator, attachment rejection, FR-60), `removeresponse` (moderator), and `listresponses` (tier 0); register via `RegisterResponseCommands` in `RegisterAllCommands`. Name validation through the shared `^[a-z0-9_-]{1,32}$` helper, built-in collision rejection (FR-52/FR-53), DM declines (FR-59).
  4. **Tests** — add `BotTests/TestCustomResponses.cs` covering the NFR-15 scenarios.
  5. **Verify & document** — `dotnet build HambotV2.sln` and `dotnet test HambotV2.sln` (warning-free), remove the *(planned)* markers from design §3.7, and tick this task.
- **Acceptance criteria**:
  1. Custom responses are scoped per guild and persisted in PostgreSQL: each records its name, content, the Discord ID of the user who last modified it, and created/updated timestamps. A response defined in one guild has no effect in any other guild.
  2. Response names must be a single lowercase token of 1–32 characters (lowercase letters, digits, hyphen, underscore). Invalid names are rejected with an in-channel error.
  3. A response may not take the name of a registered built-in command; creation rejects such names with an in-channel error. Built-in commands always win dispatch — a custom response can never shadow a built-in, including built-ins added later.
  4. When a command token matches no built-in command, the bot looks up the guild's custom responses before replying "unavailable"; a match replies in-channel with the stored content.
  5. Custom response invocations go through the same checks as built-in commands: the per-(guild, user, command) cooldown throttle keyed on the response name, and a permission check defaulting to tier 0 whose required tier is adjustable at runtime via `!commandpermission` (stored as regular `CommandMetaData`).
  6. `!addresponse <name> <content...>` creates the response, or — if the name already exists in the guild — updates its content and modification fields; the reply distinguishes "created" from "updated". Empty content is rejected; content is capped at 1000 characters. Default required tier: **moderator**.
  7. `!removeresponse <name>` deletes the response and confirms; an unknown name produces an in-channel error. Default required tier: **moderator**.
  8. `!listresponses` lists the guild's response names alphabetically; a guild with none gets a message saying so. Default tier: 0.
  9. Custom responses do not apply in direct messages (they are guild state): in DMs an unknown token keeps the normal "unavailable" reply. The management commands decline in DMs.
  10. `!addresponse` invocations carrying file or image attachments are rejected in-channel and nothing is stored (FR-60); dispatched responses are plain text only — links are stored and sent verbatim, and the bot uploads no files.
  11. Unit tests cover: name/content validation, built-in collision rejection, create-vs-update behavior, removal, per-guild isolation, dispatch order (built-in wins over custom, custom wins over "unavailable"), cooldown and permission gating of response dispatch, attachment rejection, and DM behavior.

### [ ] 3. Reminders — timed direct-message reminders *(FR-61–FR-71, NFR-16; design: §3.8)*

- **Problem**: HambotV2 cannot remind users of things. The old bot's `remindme` used in-memory timers: reminders due during downtime were lost, delivery failures were silently swallowed, and reminders could not be listed or cancelled.
- **Steps**:
  1. **Entity & migration** — add the nested `Reminder` class to `DatabaseContext` (key `OwnerDiscordId + Number`; `Message`, `DueAtUtc`, `GuildId` nullable, `CreatedAt`, `FailedAtUtc`, §3.4 table), expose it on `IDatabaseContext`, scaffold the `AddReminders` migration.
  2. **Duration parser** — implement the pure `ReminderDurationParser` (units `s`/`m`/`h`/`d`/`w` plus long forms, compact and spaced forms, summed tokens; positive totals only, no bounds — FR-62/FR-63), covered by direct unit tests before any command uses it.
  3. **Delivery service** — add `Reminders/ReminderScheduler` (singleton wake-up signal) and the hosted `Reminders/ReminderDeliveryService` (§3.8): sleep until the earliest pending due time (bounded 10-second fallback poll), deliver due reminders as DMs through fresh `IServiceScopeFactory` scopes, retry failures a bounded number of times then mark `FailedAtUtc` and log, all through `TimeProvider`; register both in `Program.cs` (§2.1).
  4. **Commands** — create `Commands/ReminderCommands/` with `remindme` (persist + confirm + notify scheduler, per-user cap of 10, FR-64/FR-70), `reminders` (list soonest-first, capped at 10, FR-68), and `cancelreminder` (own reminders only + notify scheduler, FR-69); register via `RegisterReminderCommands` in `RegisterAllCommands`. All tier 0 and fully usable in DMs (FR-71).
  5. **Tests** — add `BotTests/TestReminders.cs` (`FakeTimeProvider` + EF InMemory + Moq) covering the NFR-16 scenarios, including delivery as time advances, re-scheduling on an earlier reminder, restart recovery, and the bounded-failure path.
  6. **Verify & document** — `dotnet build HambotV2.sln` and `dotnet test HambotV2.sln` (warning-free), remove the *(planned)* markers from design §3.8 (including the §2/§2.1 planned entries), and tick this task.
- **Acceptance criteria**:
  1. `!remindme <duration> <message...>` parses the duration from leading number+unit tokens — units `s`, `m`, `h`, `d`, `w` with long forms (`seconds` … `weeks`) accepted, compact (`10d 5h`) and spaced (`10 d 5 h`) forms both valid, multiple tokens summed (e.g. `!remindme 1d 12h water the plants`). The message is the remainder of the invocation, non-empty and capped at 500 characters.
  2. The total duration must be positive — there is **no minimum or maximum bound**: durations of years are accepted. Anything unparsable or non-positive produces an in-channel error with a usage hint, and nothing is stored.
  3. On success the reminder is persisted (owner Discord ID, message, due time in UTC, guild ID — null when created in a DM) and the bot confirms in-channel, echoing the message and the due time.
  4. Reminders are personal and work from both guild channels and direct messages.
  5. A hosted background service delivers each reminder as a direct message to the owner containing the reminder message, **second-accurate**: it schedules its next wake-up at the earliest pending due time instead of a fixed coarse poll, re-scheduling when a newly created (or cancelled) reminder becomes the earliest, with a bounded fallback poll (every 10 seconds) to cover clock drift and missed wake-ups. Delivered reminders are removed from the pending set. Delivery never blocks or crashes the service.
  6. Reminders survive restarts: the pending set is persisted, and a reminder that became due while the bot was down is delivered once the service starts.
  7. A delivery failure (e.g. closed DMs) is retried a bounded number of times and then marked failed and logged; it never blocks other reminders and never produces a user-facing crash.
  8. `!reminders` lists the invoking user's pending reminders (number, due time, message), soonest first, capped at 10 entries.
  9. `!cancelreminder <number>` cancels one of the invoking user's own pending reminders (numbers from `!reminders`) and confirms; users can never see or cancel another user's reminders. Unknown or foreign numbers produce an in-channel error.
  10. A per-user cap of 10 pending reminders is enforced; exceeding it produces an in-channel error instead of storing the reminder.
  11. All three commands default to tier 0, are subject to cooldown throttling, provide help text, and — because reminders are user-scoped, not guild-scoped — remain fully usable in direct messages.
  12. Unit tests (using `FakeTimeProvider` and EF InMemory) cover: duration parsing (units, long/compact/spaced forms, sums, invalid input, non-positive durations, multi-year durations), message validation, confirmation reply, per-user isolation and caps, list and cancel behavior, due-vs-not-due delivery as time advances (including re-scheduling when an earlier reminder is added), restart recovery (a persisted due reminder is delivered when the service starts), and the bounded-failure path.

### [ ] 4. Jenkins auto-deploy — deploy to the server on a successful build *(infrastructure; NFR-6/NFR-8; design: §5 Deployment)*

- **Problem**: The Jenkins pipeline (`Jenkinsfile`) only builds and tests the solution (NFR-6). Getting a change onto the server is a manual, error-prone step — every release currently requires someone to copy the code to the server and run the container build/update by hand, so what runs in production can silently drift from what passed CI.
- **Steps**:
  1. **Deployment decisions & server prep** — pin down the deployment facts this task needs: the target server (host, SSH user), whether the stack runs via the root `docker-compose.yml` (bot + PostgreSQL) or plain Docker, and where secrets live. Recommended: the server keeps its own gitignored `.env` (filled once from `.env.example`, never touched by the pipeline) and the pipeline never sees secret values; Jenkins gets an SSH credential for the deploy user.
  2. **Deploy stage** — extend `Jenkinsfile` with a `Deploy` stage after `Test`, gated so it runs only when the build and tests succeeded *and* the commit is on `master` (`when { branch 'master' }`) — feature branches and PR builds never deploy. Inside the stage: transfer the working tree to the server (e.g. `rsync`/`git archive` over SSH, excluding `TestResults/` and local build output) and run `docker compose up -d --build` remotely; secrets come from the server's `.env`, so nothing sensitive appears in the Jenkinsfile or the job's log (NFR-3/NFR-4 still apply to the pipeline). The exact transfer/build mechanics may be adjusted to what the server supports — building on the server via compose keeps image storage and architectures simple.
  3. **Post-deploy verification** — after `compose up`, make the pipeline confirm the deployment actually came up instead of failing silently: wait for the `db` healthcheck and check the bot container logs for the known startup lines ("Database migrations applied successfully", then the Discord connection initialization / gateway ready). A deployment that does not come up healthy fails the Jenkins job so it is noticed, not discovered by users.
  4. **Deploy hygiene** — `docker compose up -d --build` replaces only changed containers; a failed build or failed health check must leave the previously running container in place (deploy failure ≠ outage). Also prune dangling images on the server after successful deploys so the disk does not fill up.
  5. **Verify & document** — push a trivial change on `master`, watch Jenkins build, test, and deploy automatically, then confirm the bot reconnects and works (e.g. `!hello`); deliberately break the build once to confirm no deploy happens. Update design doc §5 (Deployment) and NFR-6 with the deploy stage so the documents keep describing the pipeline as it is.
- **Acceptance criteria**:
  1. A push to `master` that builds warning-free and passes all tests is deployed to the server automatically, with no manual steps.
  2. A failed build or failed test run never deploys anything, and non-`master` branches never deploy.
  3. Secrets are never present in the `Jenkinsfile`, the Jenkins job configuration, or the build log; the server's `.env` is the only secret source at deploy time (NFR-3/NFR-4).
  4. The pipeline verifies the deployment: the job fails if the database does not become healthy or the bot does not reach its known startup lines, and a failed deploy leaves the previously running version serving.
  5. Each deployment is traceable from the Jenkins UI (build number, commit, and the version running on the server), and the deployment procedure is documented in design doc §5 so it describes the pipeline as it actually works.
