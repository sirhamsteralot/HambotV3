using Discord;

using HambotV2.Commands;
using HambotV2.Commands.LlmCommands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using HambotV2.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace BotTests
{
    [TestClass]
    public sealed class TestLlm
    {
        private const ulong GuildId = DiscordTestDoubles.DefaultGuildId;

        private sealed class FakeLlmClient : ILlmClient
        {
            public Queue<LlmResponse> Responses { get; } = new();
            public Exception? Failure { get; set; }
            public List<string> Models { get; } = new() { "mistral-small-latest", "mistral-large-latest" };
            public List<IReadOnlyList<LlmMessage>> Calls { get; } = new();
            public int ListCalls { get; private set; }

            public Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
            {
                Calls.Add(messages);
                if (Failure is not null)
                    throw Failure;
                return Task.FromResult(Responses.Count > 0
                    ? Responses.Dequeue()
                    : new LlmResponse("default reply", 10, 5));
            }

            public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
            {
                ListCalls++;
                if (Failure is not null)
                    throw Failure;
                return Task.FromResult<IReadOnlyList<string>>(Models);
            }
        }

        private static IConfiguration Config(Dictionary<string, string?> values)
        {
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        private static LlmSettings ActiveSettings(int budget = 500, bool presence = true, bool bio = false, bool includeChat = false, string? persona = null)
        {
            return new LlmSettings
            {
                ApiKey = "test-key",
                PresenceUpdatesEnabled = presence,
                BioUpdatesEnabled = bio,
                IncludeChat = includeChat,
                DailyRequestBudget = budget,
                Persona = persona
            };
        }

        private static (LlmGate gate, LlmUsageTracker tracker, FakeLlmClient client) CreateGate(LlmSettings settings, FakeLlmClient? client = null, FakeTimeProvider? time = null)
        {
            client ??= new FakeLlmClient();
            time ??= new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var tracker = new LlmUsageTracker(time);
            var gate = new LlmGate(client, tracker, settings, NullLogger<LlmGate>.Instance);
            return (gate, tracker, client);
        }

        private static IServiceScopeFactory ScopeFactoryFor(DatabaseContext database)
        {
            var services = new ServiceCollection();
            // Singleton so a disposed scope (one per persona resolution) never disposes the shared test database.
            services.AddSingleton<IDatabaseContext>(database);
            return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        private static Mock<IPresenceService> CreatePresence(PresenceActivity? current = null)
        {
            var presence = new Mock<IPresenceService>();
            presence.SetupGet(p => p.CurrentActivity).Returns(current);
            presence.Setup(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()))
                .ReturnsAsync(true);
            presence.Setup(p => p.ApplyBioAsync(It.IsAny<string>())).ReturnsAsync(true);
            return presence;
        }

        // --- settings parsing ---

        [TestMethod]
        public void Settings_Absent_Section_Uses_Defaults_And_Is_Inactive_Without_Key()
        {
            LlmSettings settings = LlmSettings.FromConfiguration(Config(new Dictionary<string, string?>()));

            Assert.IsNull(settings.ApiKey);
            Assert.IsFalse(settings.Active);
            Assert.AreEqual(LlmSettings.DefaultModel, settings.Model);
            Assert.AreEqual(60, settings.PresenceUpdatesIntervalMinutes);
            Assert.AreEqual(60, settings.BioUpdatesIntervalMinutes);
            Assert.AreEqual(20, settings.ChatContextMaxMessages);
            Assert.AreEqual(4000, settings.ChatContextMaxCharacters);
            Assert.AreEqual(500, settings.DailyRequestBudget);
            Assert.IsFalse(settings.PresenceUpdatesEnabled);
            Assert.IsFalse(settings.IncludeChat);
            Assert.IsFalse(settings.BioUpdatesEnabled);
        }

        [TestMethod]
        public void Settings_Bio_Interval_Parses_Valid_And_Rejects_Invalid_Values()
        {
            LlmSettings settings = LlmSettings.FromConfiguration(Config(new Dictionary<string, string?>
            {
                ["Llm:BioUpdates:IntervalMinutes"] = "15"
            }));
            Assert.AreEqual(15, settings.BioUpdatesIntervalMinutes);

            LlmSettings fallback = LlmSettings.FromConfiguration(Config(new Dictionary<string, string?>
            {
                ["Llm:BioUpdates:IntervalMinutes"] = "0"
            }));
            Assert.AreEqual(60, fallback.BioUpdatesIntervalMinutes, "zero/negative falls back to the default");
        }

        [TestMethod]
        public void Settings_Invalid_Entries_Fall_Back_To_Defaults_Zero_Budget_Disables()
        {
            LlmSettings settings = LlmSettings.FromConfiguration(Config(new Dictionary<string, string?>
            {
                ["Llm:ApiKey"] = "key",
                ["Llm:PresenceUpdates:IntervalMinutes"] = "-5",
                ["Llm:BioUpdates:IntervalMinutes"] = "not-a-number",
                ["Llm:ChatContext:MaxMessages"] = "not-a-number",
                ["Llm:DailyRequestBudget"] = "0",
                ["Llm:PresenceUpdates:Enabled"] = "true"
            }));

            Assert.AreEqual(60, settings.PresenceUpdatesIntervalMinutes);
            Assert.AreEqual(60, settings.BioUpdatesIntervalMinutes);
            Assert.AreEqual(20, settings.ChatContextMaxMessages);
            Assert.AreEqual(0, settings.DailyRequestBudget);
            Assert.IsFalse(settings.Active, "a zero budget disables the feature set like an absent key");
            Assert.IsTrue(settings.PresenceUpdatesEnabled);
        }

        // --- output validation ---

        [TestMethod]
        public void Presence_Validation_Accepts_Single_Line_Up_To_128_And_Rejects_The_Rest()
        {
            Assert.IsTrue(LlmOutputValidator.TryValidatePresence(new string('x', 128), out _));
            Assert.IsFalse(LlmOutputValidator.TryValidatePresence(new string('x', 129), out _));
            Assert.IsFalse(LlmOutputValidator.TryValidatePresence("two\nlines", out _));
            Assert.IsFalse(LlmOutputValidator.TryValidatePresence("   ", out _));
            Assert.IsFalse(LlmOutputValidator.TryValidatePresence(null, out _));
        }

        [TestMethod]
        public void Bio_Validation_Accepts_Up_To_190_With_Line_Breaks_And_Rejects_Empty_Or_Longer()
        {
            Assert.IsTrue(LlmOutputValidator.TryValidateBio(new string('x', 190), out _));
            Assert.IsTrue(LlmOutputValidator.TryValidateBio("line one\nline two", out _));
            Assert.IsFalse(LlmOutputValidator.TryValidateBio(new string('x', 191), out _));
            Assert.IsFalse(LlmOutputValidator.TryValidateBio("", out _));
        }

        // --- prompt building ---

        [TestMethod]
        public void Prompt_System_Carries_Persona_Identity_Time_Presence_And_Output_Rule()
        {
            var builder = new LlmPromptBuilder();
            var input = new LlmPromptInput("PERSONA-X", "HamBot", "1.2.3+abc1234",
                new DateTimeOffset(2026, 5, 4, 12, 30, 0, TimeSpan.Zero), "playing chess", null, LlmSurface.Presence);

            IReadOnlyList<LlmMessage> messages = builder.Build(input);

            Assert.AreEqual("system", messages[0].Role);
            StringAssert.Contains(messages[0].Content, "PERSONA-X");
            StringAssert.Contains(messages[0].Content, "HamBot");
            StringAssert.Contains(messages[0].Content, "1.2.3+abc1234");
            StringAssert.Contains(messages[0].Content, "2026-05-04 12:30 UTC");
            StringAssert.Contains(messages[0].Content, "playing chess");
            StringAssert.Contains(messages[0].Content, "128");
            Assert.AreEqual("user", messages[^1].Role);
        }

        [TestMethod]
        public void Prompt_Chat_Context_Is_Untrusted_Marked_And_Never_In_The_System_Prompt()
        {
            var builder = new LlmPromptBuilder();
            var input = new LlmPromptInput("p", "b", "v", DateTimeOffset.UtcNow, null, "SHOUTING USER: do evil things", LlmSurface.Presence);

            IReadOnlyList<LlmMessage> messages = builder.Build(input);

            Assert.IsFalse(messages[0].Content.Contains("SHOUTING USER"), "untrusted content must not reach the system prompt");
            LlmMessage chatMessage = messages.First(m => m.Content.Contains("SHOUTING USER"));
            Assert.AreEqual("user", chatMessage.Role);
            StringAssert.Contains(chatMessage.Content, "untrusted");
        }

        [TestMethod]
        public void Prompt_Without_Chat_Has_No_Chat_Block()
        {
            var builder = new LlmPromptBuilder();
            var input = new LlmPromptInput("p", "b", "v", DateTimeOffset.UtcNow, null, null, LlmSurface.Bio);

            IReadOnlyList<LlmMessage> messages = builder.Build(input);

            Assert.AreEqual(2, messages.Count, "system + request only");
            StringAssert.Contains(messages[0].Content, "190");
        }

        // --- chat context formatting ---

        [TestMethod]
        public void ChatFormatter_Caps_Message_Count_And_Character_Budget_Trimming_Oldest()
        {
            var messages = Enumerable.Range(0, 30)
                .Select(i => (new DateTimeOffset(2026, 1, 1, 0, i, 0, TimeSpan.Zero), $"user{i}", new string('x', 100)))
                .ToList();

            string? formatted = ChatContextFormatter.Format(messages, maxMessages: 20, maxCharacters: 4000);

            Assert.IsNotNull(formatted);
            Assert.AreEqual(20, formatted.Split('\n').Length, "message cap applies");
            Assert.IsTrue(formatted.Length <= 4000 + 20, "character cap trims whole lines from the oldest end");
            StringAssert.Contains(formatted, "user0");
        }

        [TestMethod]
        public void ChatFormatter_Empty_Input_Returns_Null_And_Newlines_Are_Flattened()
        {
            Assert.IsNull(ChatContextFormatter.Format([], 20, 4000));

            string? formatted = ChatContextFormatter.Format(
                [(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "u", "line one\nline two")], 20, 4000);
            Assert.IsNotNull(formatted);
            Assert.IsFalse(formatted.Contains('\n') && formatted.Split('\n').Length > 1 && formatted.Contains("line one\n"), "message text newlines are flattened");
            StringAssert.Contains(formatted, "line one line two");
        }

        // --- usage tracker ---

        [TestMethod]
        public void UsageTracker_Records_Per_Feature_And_Rolls_Over_On_Utc_Day_Change()
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 23, 59, 0, TimeSpan.Zero));
            var tracker = new LlmUsageTracker(time);

            tracker.Record("presence", 100, 50);
            tracker.RecordFailure();
            tracker.RecordFailure();

            Assert.AreEqual(1, tracker.RequestsToday);
            Assert.AreEqual(2, tracker.ConsecutiveFailures);
            Assert.AreEqual(1, tracker.FeaturesToday["presence"].Requests);
            Assert.AreEqual(100, tracker.FeaturesToday["presence"].InputTokens);

            time.Advance(TimeSpan.FromMinutes(2));

            Assert.AreEqual(0, tracker.RequestsToday, "counters reset on UTC-day rollover");
            Assert.AreEqual(0, tracker.ConsecutiveFailures);
            Assert.AreEqual(0, tracker.FeaturesToday.Count);
        }

        [TestMethod]
        public void UsageTracker_Success_Resets_Consecutive_Failures()
        {
            var tracker = new LlmUsageTracker(new FakeTimeProvider());
            tracker.RecordFailure();
            tracker.RecordFailure();
            tracker.RecordSuccess();
            Assert.AreEqual(0, tracker.ConsecutiveFailures);
        }

        // --- gate ---

        [TestMethod]
        public async Task Gate_Without_Client_Is_Inactive_And_Blocks_Calls()
        {
            var (gate, _, _) = CreateGate(ActiveSettings(), client: null!);
            // null client simulates "no API key configured"
            var gateWithoutClient = new LlmGate(null, new LlmUsageTracker(new FakeTimeProvider()), ActiveSettings(), NullLogger<LlmGate>.Instance);

            Assert.IsFalse(gateWithoutClient.IsConfigured);
            Assert.IsNull(await gateWithoutClient.ChatAsync("presence", [new LlmMessage("user", "hi")]));
            Assert.IsFalse(gate.IsConfigured == false, "sanity: fake client present means configured");
            Assert.IsTrue(gate.IsConfigured);
        }

        [TestMethod]
        public void Gate_Resolves_From_Di_Without_A_Registered_Client()
        {
            // Mirrors Program.cs: ILlmClient is registered only when an API key is configured.
            // With no key the gate must still activate (null client => inactive) instead of
            // crashing host startup with an unresolvable ILlmClient.
            LlmSettings settings = LlmSettings.FromConfiguration(Config(new Dictionary<string, string?>()));
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(settings);
            services.AddSingleton<ILlmUsageTracker, LlmUsageTracker>();
            services.AddSingleton<ILogger<LlmGate>>(NullLogger<LlmGate>.Instance);
            services.AddSingleton<ILlmGate>(serviceProvider => new LlmGate(
                serviceProvider.GetService<ILlmClient>(),
                serviceProvider.GetRequiredService<ILlmUsageTracker>(),
                settings,
                serviceProvider.GetRequiredService<ILogger<LlmGate>>()));

            using var provider = services.BuildServiceProvider();
            ILlmGate gate = provider.GetRequiredService<ILlmGate>();

            Assert.IsFalse(gate.IsConfigured);
        }

        [TestMethod]
        public async Task Gate_KillSwitch_Blocks_Calls_Until_Cleared()
        {
            var (gate, _, client) = CreateGate(ActiveSettings());

            gate.SetKillSwitch(true);
            Assert.IsNull(await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]));
            Assert.AreEqual(0, client.Calls.Count);

            gate.SetKillSwitch(false);
            Assert.IsNotNull(await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]));
            Assert.AreEqual(1, client.Calls.Count);
        }

        [TestMethod]
        public async Task Gate_Daily_Budget_Hard_Stops_For_The_Rest_Of_The_Utc_Day()
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
            var (gate, tracker, client) = CreateGate(ActiveSettings(budget: 2), time: time);

            Assert.IsNotNull(await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]));
            Assert.IsNotNull(await gate.ChatAsync("bio", [new LlmMessage("user", "hi")]));
            Assert.IsTrue(gate.BudgetExhausted);
            Assert.IsNull(await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]), "third call is blocked");
            Assert.AreEqual(2, client.Calls.Count);
            Assert.AreEqual(2, tracker.RequestsToday);

            time.Advance(TimeSpan.FromHours(13)); // next UTC day
            Assert.IsFalse(gate.BudgetExhausted, "budget resets on UTC-day rollover");
            Assert.IsNotNull(await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]));
        }

        [TestMethod]
        public async Task Gate_Success_Accounts_Usage_And_Clears_Failures_Failure_Returns_Null_And_Counts()
        {
            var (gate, tracker, client) = CreateGate(ActiveSettings());
            client.Responses.Enqueue(new LlmResponse("ok", 42, 7));

            LlmResponse? response = await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]);

            Assert.AreEqual("ok", response?.Content);
            Assert.AreEqual(1, tracker.FeaturesToday["presence"].Requests);
            Assert.AreEqual(42, tracker.FeaturesToday["presence"].InputTokens);
            Assert.AreEqual(7, tracker.FeaturesToday["presence"].OutputTokens);
            Assert.AreEqual(0, tracker.ConsecutiveFailures);

            client.Failure = new LlmClientException("API down");
            Assert.IsNull(await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]), "a failed call returns null instead of throwing");
            Assert.AreEqual(1, tracker.ConsecutiveFailures);

            gate.RecordInvalidOutput();
            Assert.AreEqual(2, tracker.ConsecutiveFailures, "invalid output counts like a failure");
        }

        // --- persona resolution ---

        [TestMethod]
        public void Persona_Resolution_Chain_Guild_Then_Global_Then_Config_Then_Seed()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var settings = ActiveSettings(persona: "CONFIG-PERSONA");

            Assert.AreEqual(LlmPersona.DefaultSeed, LlmPersona.ResolveEffective(database, null, ActiveSettings(persona: null)), "shipped seed is the last fallback");
            Assert.AreEqual("CONFIG-PERSONA", LlmPersona.ResolveEffective(database, null, settings));

            database.BotSettings.Add(new DatabaseContext.BotSetting { Key = LlmPersona.GlobalOverrideKey, Value = "GLOBAL-OVERRIDE" });
            database.SaveChanges();
            Assert.AreEqual("GLOBAL-OVERRIDE", LlmPersona.ResolveEffective(database, null, settings));

            database.GuildSettings.Add(new DatabaseContext.GuildSetting { GuildId = GuildId, Persona = "GUILD-OVERRIDE" });
            database.SaveChanges();
            Assert.AreEqual("GUILD-OVERRIDE", LlmPersona.ResolveEffective(database, GuildId, settings), "guild override wins for guild-scoped runs");
            Assert.AreEqual("GLOBAL-OVERRIDE", LlmPersona.ResolveEffective(database, null, settings), "global runs skip the guild layer");

            (string persona, string layer) = LlmPersona.ResolveGlobalWithLayer(database, settings);
            Assert.AreEqual("GLOBAL-OVERRIDE", persona);
            StringAssert.Contains(layer, "runtime override");
        }

        // --- presence/bio update loop ---

        private static PresenceUpdateService CreateService(LlmSettings settings, ILlmGate gate, Mock<IPresenceService> presence, DatabaseContext database, IChatContextCollector? chat = null)
        {
            return new PresenceUpdateService(
                settings,
                gate,
                new LlmPromptBuilder(),
                presence.Object,
                ScopeFactoryFor(database),
                Config(new Dictionary<string, string?> { ["BotConfiguration:BotName"] = "HamBot" }),
                new FakeTimeProvider(),
                NullLogger<PresenceUpdateService>.Instance,
                chat);
        }

        [TestMethod]
        public async Task Tick_Applies_Validated_Presence_Through_The_Presence_Service()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings());
            client.Responses.Enqueue(new LlmResponse("hoarding seeds", 10, 5));
            var presence = CreatePresence(new PresenceActivity(ActivityType.Watching, "old text"));
            var service = CreateService(ActiveSettings(), gate, presence, database);

            await service.RunTickAsync();

            presence.Verify(p => p.ApplyActivityAsync(ActivityType.Watching, "hoarding seeds"), Times.Once,
                "the current activity type is preserved");
            Assert.AreEqual(1, client.Calls.Count);
            StringAssert.Contains(client.Calls[0][0].Content, LlmPersona.DefaultSeed, "prompt carries the shipped persona by default");
        }

        [TestMethod]
        public async Task Tick_Uses_The_Global_Persona_Override_In_The_Prompt()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.BotSettings.Add(new DatabaseContext.BotSetting { Key = LlmPersona.GlobalOverrideKey, Value = "OVERRIDE-PERSONA" });
            database.SaveChanges();
            var (gate, _, client) = CreateGate(ActiveSettings());
            client.Responses.Enqueue(new LlmResponse("ok", 1, 1));
            var service = CreateService(ActiveSettings(), gate, CreatePresence(), database);

            await service.RunTickAsync();

            StringAssert.Contains(client.Calls[0][0].Content, "OVERRIDE-PERSONA");
        }

        [TestMethod]
        public async Task Tick_Invalid_Output_Keeps_Current_State_And_Counts_A_Failure()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, tracker, client) = CreateGate(ActiveSettings());
            client.Responses.Enqueue(new LlmResponse("two\nlines", 1, 1));
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(), gate, presence, database);

            await service.RunTickAsync();

            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
            Assert.AreEqual(1, tracker.ConsecutiveFailures);
        }

        [TestMethod]
        public async Task Tick_When_Kill_Switch_On_Does_Not_Call_The_Model()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings());
            gate.SetKillSwitch(true);
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(), gate, presence, database);

            await service.RunTickAsync();

            Assert.AreEqual(0, client.Calls.Count);
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task Tick_With_Bio_Enabled_Applies_Bio_In_The_Same_Pass()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings(bio: true));
            client.Responses.Enqueue(new LlmResponse("activity text", 1, 1));
            client.Responses.Enqueue(new LlmResponse("a fine bio", 1, 1));
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(bio: true), gate, presence, database);

            await service.RunTickAsync();

            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), "activity text"), Times.Once);
            presence.Verify(p => p.ApplyBioAsync("a fine bio"), Times.Once);
            Assert.AreEqual(2, client.Calls.Count);
        }

        [TestMethod]
        public async Task Tick_Includes_Chat_Context_Only_When_Configured()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings(includeChat: true));
            client.Responses.Enqueue(new LlmResponse("ok", 1, 1));
            var chat = new Mock<IChatContextCollector>();
            chat.Setup(c => c.CollectAsync(20, 4000, It.IsAny<CancellationToken>()))
                .ReturnsAsync("[2026-01-01 10:00 UTC] user: hello");
            var service = CreateService(ActiveSettings(includeChat: true), gate, CreatePresence(), database, chat.Object);

            await service.RunTickAsync();

            StringAssert.Contains(client.Calls[0][1].Content, "hello", "chat context rides as a data-marked user message");
            StringAssert.Contains(client.Calls[0][1].Content, "untrusted");
            Assert.IsFalse(client.Calls[0][0].Content.Contains("hello"), "chat never enters the system prompt");
        }

        [TestMethod]
        public async Task Update_Forces_A_Surface_Even_When_Its_Job_Is_Disabled()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings(presence: false, bio: false));
            client.Responses.Enqueue(new LlmResponse("manual presence", 1, 1));
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(presence: false, bio: false), gate, presence, database);

            LlmUpdateOutcome outcome = await service.UpdateAsync(LlmSurface.Presence);

            Assert.AreEqual(LlmUpdateOutcome.Applied, outcome);
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), "manual presence"), Times.Once);
            Assert.AreEqual(1, client.Calls.Count);
        }

        [TestMethod]
        public async Task Update_Bio_Uses_The_Bio_Surface_Rules()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings(presence: false, bio: false));
            client.Responses.Enqueue(new LlmResponse("a fresh bio", 1, 1));
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(presence: false, bio: false), gate, presence, database);

            LlmUpdateOutcome outcome = await service.UpdateAsync(LlmSurface.Bio);

            Assert.AreEqual(LlmUpdateOutcome.Applied, outcome);
            presence.Verify(p => p.ApplyBioAsync("a fresh bio"), Times.Once);
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task Update_Reports_Inactive_Without_A_Configured_Client()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var gate = new LlmGate(null, new LlmUsageTracker(new FakeTimeProvider()), ActiveSettings(), NullLogger<LlmGate>.Instance);
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(), gate, presence, database);

            LlmUpdateOutcome outcome = await service.UpdateAsync(LlmSurface.Presence);

            Assert.AreEqual(LlmUpdateOutcome.Inactive, outcome);
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task Update_Reports_Blocked_When_The_Kill_Switch_Is_On()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings());
            gate.SetKillSwitch(true);
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(), gate, presence, database);

            LlmUpdateOutcome outcome = await service.UpdateAsync(LlmSurface.Presence);

            Assert.AreEqual(LlmUpdateOutcome.Blocked, outcome);
            Assert.AreEqual(0, client.Calls.Count);
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task Update_Invalid_Output_Is_Reported_And_Keeps_The_Current_State()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, tracker, client) = CreateGate(ActiveSettings());
            client.Responses.Enqueue(new LlmResponse("two\nlines", 1, 1));
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(), gate, presence, database);

            LlmUpdateOutcome outcome = await service.UpdateAsync(LlmSurface.Presence);

            Assert.AreEqual(LlmUpdateOutcome.InvalidOutput, outcome);
            Assert.AreEqual(1, tracker.ConsecutiveFailures);
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        // --- commands ---

        private static async Task<List<string>> ExecuteAsync(ICommand command, DatabaseContext database, string content, ulong authorId, bool directMessage = false)
        {
            var sentMessages = new List<string>();
            var message = directMessage
                ? DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages)
                : DiscordTestDoubles.CreateGuildMessage(content, authorId, guildOwnerId: 100, sentMessages);
            var context = DiscordTestDoubles.CreateContext(database, guildOwnerId: 100, guildId: directMessage ? null : GuildId);
            await command.ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        [TestMethod]
        public async Task LlmCommand_Status_Reports_State_And_Off_On_Toggle_The_Kill_Switch()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings());
            client.Responses.Enqueue(new LlmResponse("ok", 3, 2));
            await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]);
            var command = new LlmCommand(gate);

            var status = await ExecuteAsync(command, database, "!llm status", authorId: 250);
            StringAssert.Contains(status[0], "LLM: active");
            StringAssert.Contains(status[0], "Requests today: 1 / budget 500");
            StringAssert.Contains(status[0], "presence: 1 requests, 3 tokens in / 2 out");

            var off = await ExecuteAsync(command, database, "!llm off", authorId: 250);
            StringAssert.Contains(off[0], "OFF");
            Assert.IsTrue(gate.KillSwitchActive);
            Assert.IsNull(await gate.ChatAsync("presence", [new LlmMessage("user", "hi")]), "kill switch blocks calls");

            var on = await ExecuteAsync(command, database, "!llm on", authorId: 250);
            StringAssert.Contains(on[0], "ON");
            Assert.IsFalse(gate.KillSwitchActive);
        }

        [TestMethod]
        public async Task LlmCommand_Without_Gate_Reports_Not_Configured()
        {
            var replies = await ExecuteAsync(new LlmCommand(null), DiscordTestDoubles.CreateDatabase(), "!llm", authorId: 250);
            StringAssert.Contains(replies[0], "not configured");
        }

        [TestMethod]
        public async Task PersonaCommand_Set_Show_Clear_Persists_The_Global_Override()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var command = new PersonaCommand(ActiveSettings(persona: null));

            var show = await ExecuteAsync(command, database, "!persona", authorId: 200);
            StringAssert.Contains(show[0], "shipped default");

            var set = await ExecuteAsync(command, database, "!persona A very dramatic hamster", authorId: 200);
            StringAssert.Contains(set[0], "override set");
            Assert.AreEqual("A very dramatic hamster",
                database.BotSettings.Single(s => s.Key == LlmPersona.GlobalOverrideKey).Value);

            var showAgain = await ExecuteAsync(command, database, "!persona", authorId: 200);
            StringAssert.Contains(showAgain[0], "runtime override");
            StringAssert.Contains(showAgain[0], "A very dramatic hamster");

            var clear = await ExecuteAsync(command, database, "!persona clear", authorId: 200);
            StringAssert.Contains(clear[0], "cleared");
            Assert.AreEqual(0, database.BotSettings.Count(s => s.Key == LlmPersona.GlobalOverrideKey));
        }

        [TestMethod]
        public async Task PersonaCommand_Rejects_Overlong_Persona()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var command = new PersonaCommand();

            var replies = await ExecuteAsync(command, database, "!persona " + new string('x', LlmPersona.MaxPersonaLength + 1), authorId: 200);

            StringAssert.Contains(replies[0], "at most 2000 characters");
            Assert.AreEqual(0, database.BotSettings.Count());
        }

        [TestMethod]
        public async Task SettingsCommand_Persona_Manages_The_Guild_Override()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var command = new SettingsCommand();

            var show = await ExecuteAsync(command, database, "!settings persona", authorId: 200);
            StringAssert.Contains(show[0], "none set");

            await ExecuteAsync(command, database, "!settings persona Guild specific voice", authorId: 200);
            Assert.AreEqual("Guild specific voice",
                database.GuildSettings.Single(s => s.GuildId == GuildId).Persona);

            var showAgain = await ExecuteAsync(command, database, "!settings persona", authorId: 200);
            StringAssert.Contains(showAgain[0], "Guild specific voice");

            await ExecuteAsync(command, database, "!settings persona clear", authorId: 200);
            Assert.IsNull(database.GuildSettings.Single(s => s.GuildId == GuildId).Persona);
        }

        [TestMethod]
        public async Task SettingsCommand_Persona_Declines_In_Direct_Messages()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteAsync(new SettingsCommand(), database, "!settings persona hi", authorId: 200, directMessage: true);

            StringAssert.Contains(replies[0], "only available in servers");
        }

        // --- model override & listing ---

        [TestMethod]
        public void Model_Resolution_Chain_Override_Then_Config_Then_Default()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            Assert.AreEqual(LlmSettings.DefaultModel, LlmRuntimeSettings.ResolveModel(database, ActiveSettings()));
            Assert.AreEqual("custom-model", LlmRuntimeSettings.ResolveModel(database, ActiveSettings() with { Model = "custom-model" }));

            database.BotSettings.Add(new DatabaseContext.BotSetting { Key = LlmRuntimeSettings.ModelKey, Value = "override-model" });
            database.SaveChanges();

            (string model, string layer) = LlmRuntimeSettings.ResolveModelWithLayer(database, ActiveSettings() with { Model = "custom-model" });
            Assert.AreEqual("override-model", model);
            StringAssert.Contains(layer, "runtime override");
        }

        [TestMethod]
        public void ModelResolver_Reads_The_Override_From_A_Fresh_Scope()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.BotSettings.Add(new DatabaseContext.BotSetting { Key = LlmRuntimeSettings.ModelKey, Value = "scoped-model" });
            database.SaveChanges();

            var resolver = new LlmModelResolver(ScopeFactoryFor(database), ActiveSettings());

            Assert.AreEqual("scoped-model", resolver.Resolve());
        }

        [TestMethod]
        public async Task ModelCommand_List_Shows_Api_Models_And_Set_Validates_Against_Them()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings());
            var command = new ModelCommand(gate, ActiveSettings());

            var list = await ExecuteAsync(command, database, "!model list", authorId: 200);
            StringAssert.Contains(list[0], "mistral-small-latest");
            StringAssert.Contains(list[0], "mistral-large-latest");
            Assert.AreEqual(1, client.ListCalls);

            var badSet = await ExecuteAsync(command, database, "!model no-such-model", authorId: 200);
            StringAssert.Contains(badSet[0], "Unknown model 'no-such-model'");
            Assert.AreEqual(0, database.BotSettings.Count(s => s.Key == LlmRuntimeSettings.ModelKey), "an unlisted model is never stored");

            var set = await ExecuteAsync(command, database, "!model mistral-large-latest", authorId: 200);
            StringAssert.Contains(set[0], "override set to 'mistral-large-latest'");
            Assert.AreEqual("mistral-large-latest",
                database.BotSettings.Single(s => s.Key == LlmRuntimeSettings.ModelKey).Value);

            var show = await ExecuteAsync(command, database, "!model", authorId: 200);
            StringAssert.Contains(show[0], "mistral-large-latest");
            StringAssert.Contains(show[0], "runtime override");

            var clear = await ExecuteAsync(command, database, "!model clear", authorId: 200);
            StringAssert.Contains(clear[0], "cleared");
            Assert.AreEqual(0, database.BotSettings.Count(s => s.Key == LlmRuntimeSettings.ModelKey));
        }

        [TestMethod]
        public async Task ModelCommand_Without_Gate_Reports_Not_Configured_And_List_Fails_Closed()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var command = new ModelCommand(null);

            var show = await ExecuteAsync(command, database, "!model", authorId: 200);
            StringAssert.Contains(show[0], "not configured");

            var list = await ExecuteAsync(command, database, "!model list", authorId: 200);
            StringAssert.Contains(list[0], "not configured");
        }

        [TestMethod]
        public async Task Gate_ListModels_Respects_Kill_Switch_And_Budget_And_Accounts_Requests()
        {
            var (gate, tracker, client) = CreateGate(ActiveSettings(budget: 1));

            Assert.IsNotNull(await gate.ListModelsAsync("models"));
            Assert.AreEqual(1, tracker.RequestsToday, "a list call counts as a request");
            Assert.IsTrue(gate.BudgetExhausted);
            Assert.IsNull(await gate.ListModelsAsync("models"), "budget exhausted blocks listing too");
            Assert.AreEqual(1, client.ListCalls);

            gate.SetKillSwitch(true);
            var (gate2, _, client2) = CreateGate(ActiveSettings());
            gate2.SetKillSwitch(true);
            Assert.IsNull(await gate2.ListModelsAsync("models"));
            Assert.AreEqual(0, client2.ListCalls);

            var failingClient = new FakeLlmClient { Failure = new LlmClientException("API down") };
            var (gate3, tracker3, _) = CreateGate(ActiveSettings(), client: failingClient);
            Assert.IsNull(await gate3.ListModelsAsync("models"), "a failed list returns null instead of throwing");
            Assert.AreEqual(1, tracker3.ConsecutiveFailures);
        }

        [TestMethod]
        public async Task LlmCommand_Status_Includes_The_Effective_Model()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.BotSettings.Add(new DatabaseContext.BotSetting { Key = LlmRuntimeSettings.ModelKey, Value = "status-model" });
            database.SaveChanges();
            var (gate, _, _) = CreateGate(ActiveSettings());

            var status = await ExecuteAsync(new LlmCommand(gate, ActiveSettings()), database, "!llm", authorId: 250);

            StringAssert.Contains(status[0], "Model: status-model (runtime override");
        }

        [TestMethod]
        public async Task LlmCommand_Update_Runs_Both_Surfaces_Even_When_The_Jobs_Are_Disabled()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var settings = ActiveSettings(presence: false, bio: false);
            var (gate, _, client) = CreateGate(settings);
            client.Responses.Enqueue(new LlmResponse("new activity", 1, 1));
            client.Responses.Enqueue(new LlmResponse("new bio", 1, 1));
            var presence = CreatePresence();
            var service = CreateService(settings, gate, presence, database);
            var command = new LlmCommand(gate, settings, service);

            var replies = await ExecuteAsync(command, database, "!llm update", authorId: 250);

            StringAssert.Contains(replies[0], "Presence update applied.");
            StringAssert.Contains(replies[0], "Bio update applied.");
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), "new activity"), Times.Once);
            presence.Verify(p => p.ApplyBioAsync("new bio"), Times.Once);
            Assert.AreEqual(2, client.Calls.Count);
        }

        [TestMethod]
        public async Task LlmCommand_Update_With_A_Surface_Argument_Runs_Only_That_Surface()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings());
            client.Responses.Enqueue(new LlmResponse("bio only", 1, 1));
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(), gate, presence, database);
            var command = new LlmCommand(gate, ActiveSettings(), service);

            var replies = await ExecuteAsync(command, database, "!llm update bio", authorId: 250);

            StringAssert.Contains(replies[0], "Bio update applied.");
            Assert.AreEqual(1, client.Calls.Count);
            presence.Verify(p => p.ApplyBioAsync("bio only"), Times.Once);
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task LlmCommand_Update_With_An_Unknown_Surface_Shows_Usage()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings());
            var service = CreateService(ActiveSettings(), gate, CreatePresence(), database);
            var command = new LlmCommand(gate, ActiveSettings(), service);

            var replies = await ExecuteAsync(command, database, "!llm update banana", authorId: 250);

            StringAssert.Contains(replies[0], "Unknown surface 'banana'");
            StringAssert.Contains(replies[0], "update [presence|bio]");
            Assert.AreEqual(0, client.Calls.Count);
        }

        [TestMethod]
        public async Task LlmCommand_Update_Reports_Blocked_When_The_Kill_Switch_Is_On()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, client) = CreateGate(ActiveSettings());
            gate.SetKillSwitch(true);
            var presence = CreatePresence();
            var service = CreateService(ActiveSettings(), gate, presence, database);
            var command = new LlmCommand(gate, ActiveSettings(), service);

            var replies = await ExecuteAsync(command, database, "!llm update presence", authorId: 250);

            StringAssert.Contains(replies[0], "blocked");
            Assert.AreEqual(0, client.Calls.Count);
            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task LlmCommand_Update_Without_The_Update_Service_Reports_Not_Configured()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var (gate, _, _) = CreateGate(ActiveSettings());

            var replies = await ExecuteAsync(new LlmCommand(gate), database, "!llm update", authorId: 250);

            StringAssert.Contains(replies[0], "not configured");
        }
    }
}
