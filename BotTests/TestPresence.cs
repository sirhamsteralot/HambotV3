using Discord;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace BotTests
{
    [TestClass]
    public sealed class TestPresence
    {
        private const string LongBio = "The bot maintains a profile. "; // 33 chars; repeated below to hit the boundary.

        private static string Repeat(string text, int targetLength)
        {
            var builder = new System.Text.StringBuilder();
            while (builder.Length < targetLength)
                builder.Append(text);
            return builder.ToString(0, targetLength);
        }

        private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        private static Dictionary<string, string?> ConfigurationWithoutPresence()
        {
            return new Dictionary<string, string?>
            {
                { "Secrets:BotToken", "some-token" },
                { "ConnectionStrings:DefaultConnection", "Server=localhost;Database=hambotdb;" },
                { "BotConfiguration:BotName", "HamBot" },
                { "BotConfiguration:LogFormat", "{0}: {1}" },
                { "BotConfiguration:CommandIdentifier", "!" }
            };
        }

        [TestMethod]
        public void Absent_Presence_And_Bio_Parse_To_No_Startup_Configuration()
        {
            bool parsed = PresenceStartupConfiguration.TryParse(
                BuildConfiguration(ConfigurationWithoutPresence()), out PresenceStartupConfiguration? startup, out List<string> errors);

            Assert.IsTrue(parsed, "absent configuration is valid");
            Assert.AreEqual(0, errors.Count);
            Assert.IsNull(startup, "no activity is set and the bio is left untouched");
        }

        [TestMethod]
        public void Valid_Presence_And_Bio_Parse()
        {
            var values = ConfigurationWithoutPresence();
            values["BotConfiguration:Presence:Type"] = "WATCHING";
            values["BotConfiguration:Presence:Text"] = "the logs";
            values["BotConfiguration:Bio"] = "a small hamster\nkeeping the servers running";

            bool parsed = PresenceStartupConfiguration.TryParse(BuildConfiguration(values), out PresenceStartupConfiguration? startup, out List<string> errors);

            Assert.IsTrue(parsed);
            Assert.AreEqual(0, errors.Count);
            Assert.IsNotNull(startup);
            Assert.AreEqual(ActivityType.Watching, startup.Activity!.Type);
            Assert.AreEqual("the logs", startup.Activity.Text);
            Assert.IsTrue(startup.HasBio);
            Assert.AreEqual("a small hamster\nkeeping the servers running", startup.Bio, "line breaks are allowed");
        }

        [TestMethod]
        public void Invalid_Presence_Type_Is_An_Error_Naming_The_Key()
        {
            var values = ConfigurationWithoutPresence();
            values["BotConfiguration:Presence:Type"] = "streaming";
            values["BotConfiguration:Presence:Text"] = "the logs";

            PresenceStartupConfiguration.TryParse(BuildConfiguration(values), out _, out List<string> errors);

            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0], "BotConfiguration:Presence:Type", "the offending key is named");
        }

        [TestMethod]
        public void Text_Without_Type_And_Type_Without_Text_Are_Errors()
        {
            var textOnly = ConfigurationWithoutPresence();
            textOnly["BotConfiguration:Presence:Text"] = "the logs";
            PresenceStartupConfiguration.TryParse(BuildConfiguration(textOnly), out _, out List<string> textOnlyErrors);
            Assert.AreEqual(1, textOnlyErrors.Count);
            StringAssert.Contains(textOnlyErrors[0], "BotConfiguration:Presence:Type");

            var typeOnly = ConfigurationWithoutPresence();
            typeOnly["BotConfiguration:Presence:Type"] = "playing";
            PresenceStartupConfiguration.TryParse(BuildConfiguration(typeOnly), out _, out List<string> typeOnlyErrors);
            Assert.AreEqual(1, typeOnlyErrors.Count);
            StringAssert.Contains(typeOnlyErrors[0], "BotConfiguration:Presence:Text");
        }

        [TestMethod]
        public void Activity_Text_Boundary_Is_128_Characters()
        {
            var atLimit = ConfigurationWithoutPresence();
            atLimit["BotConfiguration:Presence:Type"] = "playing";
            atLimit["BotConfiguration:Presence:Text"] = Repeat("x", 128);
            Assert.IsTrue(PresenceStartupConfiguration.TryParse(BuildConfiguration(atLimit), out _, out List<string> atLimitErrors));
            Assert.AreEqual(0, atLimitErrors.Count);

            var overLimit = ConfigurationWithoutPresence();
            overLimit["BotConfiguration:Presence:Type"] = "playing";
            overLimit["BotConfiguration:Presence:Text"] = Repeat("x", 129);
            Assert.IsFalse(PresenceStartupConfiguration.TryParse(BuildConfiguration(overLimit), out _, out List<string> overLimitErrors));
            Assert.AreEqual(1, overLimitErrors.Count);
            StringAssert.Contains(overLimitErrors[0], "BotConfiguration:Presence:Text", "the offending key is named");
        }

        [TestMethod]
        public void Bio_Boundary_Is_190_Characters()
        {
            var atLimit = ConfigurationWithoutPresence();
            atLimit["BotConfiguration:Bio"] = Repeat(LongBio, 190);
            Assert.IsTrue(PresenceStartupConfiguration.TryParse(BuildConfiguration(atLimit), out PresenceStartupConfiguration? atLimitParsed, out List<string> atLimitErrors));
            Assert.AreEqual(0, atLimitErrors.Count);
            Assert.AreEqual(190, atLimitParsed!.Bio.Length);

            var overLimit = ConfigurationWithoutPresence();
            overLimit["BotConfiguration:Bio"] = Repeat(LongBio, 191);
            Assert.IsFalse(PresenceStartupConfiguration.TryParse(BuildConfiguration(overLimit), out _, out List<string> overLimitErrors));
            Assert.AreEqual(1, overLimitErrors.Count);
            StringAssert.Contains(overLimitErrors[0], "BotConfiguration:Bio", "the offending key is named");
        }

        private sealed class FakePresenceGateway : IPresenceGateway
        {
            private event Func<Task>? _ready;

            public event Func<Task> Ready
            {
                add => _ready += value;
                remove => _ready -= value;
            }

            public bool IsReady { get; private set; }
            public List<ulong> GuildIds { get; set; } = [];
            public string? AvatarUrl { get; set; }
            public string? BannerUrl { get; set; }

            public List<(ActivityType Type, string? Text)> ActivityCalls { get; } = [];
            public List<(ulong GuildId, string? Bio)> BioCalls { get; } = [];
            public List<(ulong GuildId, byte[]? Image)> GuildAvatarCalls { get; } = [];
            public List<(ulong GuildId, byte[]? Image)> GuildBannerCalls { get; } = [];
            public List<byte[]?> GlobalAvatarCalls { get; } = [];

            /// <summary>Operation keys that throw when called; a per-guild key like "bio:2" fails one guild only.</summary>
            public HashSet<string> FailingOperations { get; } = [];

            public int InFlight { get; private set; }
            public int MaxInFlight { get; private set; }

            public async Task SimulateReadyAsync()
            {
                IsReady = true;
                foreach (Func<Task> handler in _ready?.GetInvocationList().Cast<Func<Task>>() ?? [])
                    await handler();
            }

            public Task SetActivityAsync(ActivityType type, string? text)
                => Record("activity", null, () => ActivityCalls.Add((type, text)));

            public IReadOnlyCollection<ulong> GetGuildIds() => GuildIds;

            public Task SetGuildBioAsync(ulong guildId, string? bio)
                => Record("bio", guildId, () => BioCalls.Add((guildId, bio)));

            public Task SetGuildAvatarAsync(ulong guildId, byte[]? imageData)
                => Record("guildavatar", guildId, () => GuildAvatarCalls.Add((guildId, imageData)));

            public Task SetGuildBannerAsync(ulong guildId, byte[]? imageData)
                => Record("guildbanner", guildId, () => GuildBannerCalls.Add((guildId, imageData)));

            public Task SetGlobalAvatarAsync(byte[]? imageData)
                => Record("globalavatar", null, () => GlobalAvatarCalls.Add(imageData));

            public string? GetAvatarUrl() => AvatarUrl;

            public string? GetBannerUrl() => BannerUrl;

            private async Task Record(string operation, ulong? guildId, Action record)
            {
                InFlight++;
                MaxInFlight = Math.Max(MaxInFlight, InFlight);
                try
                {
                    // Yield so concurrent unserialized calls would interleave (serialization test).
                    await Task.Yield();

                    if (FailingOperations.Contains(operation) || (guildId is not null && FailingOperations.Contains($"{operation}:{guildId}")))
                        throw new InvalidOperationException($"Simulated Discord failure in {operation}.");

                    record();
                }
                finally
                {
                    InFlight--;
                }
            }
        }

        private static PresenceStartupConfiguration StartupConfiguration(ActivityType type = ActivityType.Watching, string text = "the logs", string? bio = "a small hamster")
        {
            return new PresenceStartupConfiguration
            {
                Activity = new PresenceActivity(type, text),
                HasBio = bio is not null,
                Bio = bio ?? string.Empty
            };
        }

        private static PresenceService CreateService(FakePresenceGateway gateway, PresenceStartupConfiguration? startupConfiguration = null)
        {
            return new PresenceService(gateway, startupConfiguration, Mock.Of<ILogger<PresenceService>>());
        }

        [TestMethod]
        public async Task Startup_Configuration_Is_Applied_On_Gateway_Ready_In_Every_Guild()
        {
            var gateway = new FakePresenceGateway { GuildIds = [1, 2] };
            var service = CreateService(gateway, StartupConfiguration(bio: "a small hamster"));

            await gateway.SimulateReadyAsync();

            CollectionAssert.AreEqual(
                new[] { (ActivityType.Watching, "the logs") },
                gateway.ActivityCalls.Select(call => (call.Type, call.Text)).ToList(),
                "the configured activity is applied once the gateway is ready");
            CollectionAssert.AreEqual(
                new[] { (1UL, "a small hamster"), (2UL, "a small hamster") },
                gateway.BioCalls.ToList(),
                "the configured bio is applied in every member guild");
            Assert.AreEqual("a small hamster", service.CurrentBio);
        }

        [TestMethod]
        public async Task Absent_Configured_Bio_Is_Left_Untouched_At_Startup()
        {
            var gateway = new FakePresenceGateway { GuildIds = [1] };
            var startup = new PresenceStartupConfiguration { Activity = null, HasBio = false };
            var service = CreateService(gateway, startup);

            await gateway.SimulateReadyAsync();

            Assert.AreEqual(0, gateway.BioCalls.Count, "a restart never wipes a hand-set bio");
            Assert.IsNull(service.CurrentBio);
        }

        [TestMethod]
        public async Task Requests_Before_Ready_Are_Deferred_And_Applied_On_Ready()
        {
            var gateway = new FakePresenceGateway { GuildIds = [1] };
            var service = CreateService(gateway);

            bool deferred = await service.ApplyActivityAsync(ActivityType.Competing, "deferred request");

            Assert.IsTrue(deferred, "a deferred request is accepted, not failed");
            Assert.AreEqual(0, gateway.ActivityCalls.Count, "nothing is sent before the gateway is ready");
            Assert.AreEqual(ActivityType.Competing, service.CurrentActivity!.Type, "Current reflects the recorded request");

            await gateway.SimulateReadyAsync();

            Assert.AreEqual(1, gateway.ActivityCalls.Count, "the deferred request lands on Ready");
            Assert.AreEqual(ActivityType.Competing, gateway.ActivityCalls[0].Type);
        }

        [TestMethod]
        public async Task Runtime_Applies_Go_Through_Immediately_Once_Ready()
        {
            var gateway = new FakePresenceGateway { GuildIds = [1, 2] };
            var service = CreateService(gateway);
            await gateway.SimulateReadyAsync();

            Assert.IsTrue(await service.ApplyActivityAsync(ActivityType.Listening, "music"));
            Assert.AreEqual(1, gateway.ActivityCalls.Count);

            Assert.IsTrue(await service.ApplyBioAsync("new bio"));
            CollectionAssert.AreEqual(
                new[] { (1UL, "new bio"), (2UL, "new bio") },
                gateway.BioCalls.ToList(),
                "runtime bio changes apply in every member guild");
        }

        [TestMethod]
        public async Task Clears_Push_Null_Activity_And_Empty_Bio()
        {
            var gateway = new FakePresenceGateway { GuildIds = [1] };
            var service = CreateService(gateway, StartupConfiguration());
            await gateway.SimulateReadyAsync();

            Assert.IsTrue(await service.ClearActivityAsync());
            Assert.IsNull(gateway.ActivityCalls[^1].Text, "a null activity text clears the activity");
            Assert.IsNull(service.CurrentActivity);

            Assert.IsTrue(await service.ClearBioAsync());
            Assert.IsNull(gateway.BioCalls[^1].Bio, "a null bio clears the per-guild bio");
            Assert.IsNull(service.CurrentBio);
        }

        [TestMethod]
        public async Task Concurrent_Updates_Are_Serialized_Last_Request_Wins()
        {
            var gateway = new FakePresenceGateway();
            var service = CreateService(gateway);
            await gateway.SimulateReadyAsync();

            Task<bool> first = service.ApplyActivityAsync(ActivityType.Playing, "first");
            Task<bool> second = service.ApplyActivityAsync(ActivityType.Watching, "second");
            await Task.WhenAll(first, second);

            Assert.AreEqual(1, gateway.MaxInFlight, "exactly one Discord apply runs at a time");
            Assert.AreEqual(2, gateway.ActivityCalls.Count);
            Assert.AreEqual("second", service.CurrentActivity!.Text, "the last request wins");
        }

        [TestMethod]
        public async Task Apply_Failure_Is_Returned_Not_Thrown_And_Other_Guilds_Are_Still_Attempted()
        {
            var gateway = new FakePresenceGateway { GuildIds = [1, 2] };
            gateway.FailingOperations.Add("activity");
            gateway.FailingOperations.Add("bio:1");
            var service = CreateService(gateway);
            await gateway.SimulateReadyAsync();

            bool activityFailed = await service.ApplyActivityAsync(ActivityType.Playing, "doomed");
            Assert.IsFalse(activityFailed, "the failure surfaces to the command path as false");

            bool bioPartiallyFailed = await service.ApplyBioAsync("partially applied");
            Assert.IsFalse(bioPartiallyFailed, "a failing guild fails the overall apply");
            CollectionAssert.Contains(gateway.BioCalls.ToList(), (2UL, "partially applied"),
                "the other guilds are still attempted");

            gateway.FailingOperations.Clear();
            Assert.IsTrue(await service.ApplyActivityAsync(ActivityType.Playing, "recovered"));
        }

        [TestMethod]
        public async Task Restart_Reapplies_The_Startup_Configuration_Not_The_Runtime_Change()
        {
            var gateway = new FakePresenceGateway { GuildIds = [1] };
            var startup = StartupConfiguration();
            var runtime = CreateService(gateway, startup);
            await gateway.SimulateReadyAsync();
            await runtime.ApplyActivityAsync(ActivityType.Playing, "runtime change");
            Assert.AreEqual("runtime change", runtime.CurrentActivity!.Text);

            // A restart: a fresh service on a fresh gateway with the same startup configuration.
            var restartedGateway = new FakePresenceGateway { GuildIds = [1] };
            var restarted = CreateService(restartedGateway, startup);
            await restartedGateway.SimulateReadyAsync();

            Assert.AreEqual(
                ("the logs", ActivityType.Watching),
                (restartedGateway.ActivityCalls.Single().Text, restartedGateway.ActivityCalls.Single().Type),
                "runtime presence changes are not persisted — the startup configuration reapplies");
        }

        [TestMethod]
        public void Current_Image_Urls_Come_From_The_Gateway()
        {
            var gateway = new FakePresenceGateway { AvatarUrl = "https://cdn.example.test/a.png", BannerUrl = "https://cdn.example.test/b.png" };
            var service = CreateService(gateway);

            Assert.AreEqual("https://cdn.example.test/a.png", service.CurrentAvatarUrl);
            Assert.AreEqual("https://cdn.example.test/b.png", service.CurrentBannerUrl);
        }

        private static async Task<List<string>> ExecuteCommandAsync(ICommand command, string content, bool directMessage = false)
        {
            var sentMessages = new List<string>();
            var message = directMessage
                ? DiscordTestDoubles.CreateDirectMessage(content, 999, sentMessages)
                : DiscordTestDoubles.CreateGuildMessage(content, 999, 999, sentMessages);
            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase(), guildId: directMessage ? null : DiscordTestDoubles.DefaultGuildId);

            await command.ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        [TestMethod]
        public void Presence_Command_Registration_Contract()
        {
            var command = new PresenceCommand();

            Assert.AreEqual("presence", command.Name);
            Assert.AreEqual(PermissionTier.BotOwner, command.DefaultPermissionTier, "presence is a global bot identity — tier 4");

            var commandService = new CommandService();
            commandService.RegisterCommands();
            Assert.IsTrue(commandService.TryGetCommand("!presence", "!", out _), "the command pipeline must resolve !presence");
        }

        [TestMethod]
        public async Task Presence_Command_Reports_The_Current_Activity()
        {
            var presence = new Mock<IPresenceService>();
            presence.SetupGet(p => p.CurrentActivity).Returns(new PresenceActivity(ActivityType.Listening, "music"));
            var command = new PresenceCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, "!presence");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "listening to music", "the reply uses Discord's phrasing");
        }

        [TestMethod]
        public async Task Presence_Command_Reports_No_Activity()
        {
            var presence = new Mock<IPresenceService>();
            presence.SetupGet(p => p.CurrentActivity).Returns((PresenceActivity?)null);
            var command = new PresenceCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, "!presence");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "No activity is set");
        }

        [TestMethod]
        public async Task Presence_Command_Applies_A_Valid_Activity()
        {
            var presence = new Mock<IPresenceService>();
            presence.Setup(p => p.ApplyActivityAsync(ActivityType.Watching, "the logs")).ReturnsAsync(true);
            var command = new PresenceCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, "!presence watching the logs");

            presence.Verify(p => p.ApplyActivityAsync(ActivityType.Watching, "the logs"), Times.Once);
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "watching the logs");
        }

        [TestMethod]
        public async Task Presence_Command_Works_In_Direct_Messages()
        {
            var presence = new Mock<IPresenceService>();
            presence.Setup(p => p.ApplyActivityAsync(ActivityType.Playing, "chess")).ReturnsAsync(true);
            var command = new PresenceCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, "!presence playing chess", directMessage: true);

            Assert.AreEqual(1, replies.Count, "presence touches no guild state, so it answers in DMs too");
            StringAssert.Contains(replies[0], "playing chess");
        }

        [TestMethod]
        [DataRow("nonsense the logs")]
        [DataRow("Streaming the logs")]
        public async Task Presence_Command_Rejects_An_Invalid_Type_In_Channel(string content)
        {
            var presence = new Mock<IPresenceService>();
            var command = new PresenceCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, $"!presence {content}");

            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never,
                "an invalid type must not touch the service");
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "Unknown activity type");
        }

        [TestMethod]
        public async Task Presence_Command_Rejects_Missing_And_Multiline_And_Over_Limit_Text()
        {
            var presence = new Mock<IPresenceService>();
            var command = new PresenceCommand(presence.Object);

            List<string> missing = await ExecuteCommandAsync(command, "!presence playing");
            StringAssert.Contains(missing[0], "missing");

            List<string> multiline = await ExecuteCommandAsync(command, "!presence playing two\nlines");
            StringAssert.Contains(multiline[0], "single line");

            List<string> overLimit = await ExecuteCommandAsync(command, $"!presence playing {Repeat("x", 129)}");
            StringAssert.Contains(overLimit[0], "at most 128 characters");
            StringAssert.Contains(overLimit[0], "129");

            presence.Verify(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task Presence_Command_Clears_The_Activity()
        {
            var presence = new Mock<IPresenceService>();
            presence.Setup(p => p.ClearActivityAsync()).ReturnsAsync(true);
            var command = new PresenceCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, "!presence clear");

            presence.Verify(p => p.ClearActivityAsync(), Times.Once);
            StringAssert.Contains(replies[0], "cleared");
        }

        [TestMethod]
        public async Task Presence_Command_Surfaces_An_Apply_Failure_In_Channel()
        {
            var presence = new Mock<IPresenceService>();
            presence.Setup(p => p.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>())).ReturnsAsync(false);
            var command = new PresenceCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, "!presence playing chess");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "Failed to update", "the apply failure surfaces as the standard in-channel error");
        }

        [TestMethod]
        public async Task Presence_Command_Without_A_Configured_Service_Reports_It()
        {
            var command = new PresenceCommand();

            List<string> replies = await ExecuteCommandAsync(command, "!presence");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "not configured");
        }

        [TestMethod]
        public void Bio_Command_Registration_Contract()
        {
            var command = new BioCommand();

            Assert.AreEqual("bio", command.Name);
            Assert.AreEqual(PermissionTier.BotOwner, command.DefaultPermissionTier, "bio is a global bot identity — tier 4");

            var commandService = new CommandService();
            commandService.RegisterCommands();
            Assert.IsTrue(commandService.TryGetCommand("!bio", "!", out _), "the command pipeline must resolve !bio");
        }

        [TestMethod]
        public async Task Bio_Command_Reports_The_Current_Bio()
        {
            var presence = new Mock<IPresenceService>();
            presence.SetupGet(p => p.CurrentBio).Returns("a small hamster");
            var command = new BioCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, "!bio");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "a small hamster");
        }

        [TestMethod]
        public async Task Bio_Command_Applies_Text_With_Line_Breaks_Preserved()
        {
            var presence = new Mock<IPresenceService>();
            presence.Setup(p => p.ApplyBioAsync(It.IsAny<string>())).ReturnsAsync(true);
            var command = new BioCommand(presence.Object);

            List<string> replies = await ExecuteCommandAsync(command, "!bio first line\nsecond line");

            presence.Verify(p => p.ApplyBioAsync("first line\nsecond line"), Times.Once,
                "the raw text after the command token is preserved verbatim");
            StringAssert.Contains(replies[0], "first line\nsecond line");
        }

        [TestMethod]
        public async Task Bio_Command_Accepts_The_Boundary_And_Rejects_Over_Limit()
        {
            var presence = new Mock<IPresenceService>();
            presence.Setup(p => p.ApplyBioAsync(It.IsAny<string>())).ReturnsAsync(true);
            var command = new BioCommand(presence.Object);

            List<string> atLimit = await ExecuteCommandAsync(command, $"!bio {Repeat(LongBio, 190)}");
            presence.Verify(p => p.ApplyBioAsync(It.Is<string>(bio => bio.Length == 190)), Times.Once);
            StringAssert.Contains(atLimit[0], "The bio was set");

            List<string> overLimit = await ExecuteCommandAsync(command, $"!bio {Repeat(LongBio, 191)}");
            presence.Verify(p => p.ApplyBioAsync(It.Is<string>(bio => bio.Length == 191)), Times.Never,
                "over-limit text must not reach the service");
            StringAssert.Contains(overLimit[0], "at most 190 characters");
        }

        [TestMethod]
        public async Task Bio_Command_Clears_And_Surfaces_Failures_And_Works_In_DMs()
        {
            var presence = new Mock<IPresenceService>();
            presence.Setup(p => p.ClearBioAsync()).ReturnsAsync(true);
            var command = new BioCommand(presence.Object);

            List<string> cleared = await ExecuteCommandAsync(command, "!bio clear", directMessage: true);
            presence.Verify(p => p.ClearBioAsync(), Times.Once);
            StringAssert.Contains(cleared[0], "cleared");

            presence.Setup(p => p.ClearBioAsync()).ReturnsAsync(false);
            List<string> failed = await ExecuteCommandAsync(command, "!bio clear");
            StringAssert.Contains(failed[0], "Failed to update", "the apply failure surfaces in-channel");
        }
    }
}
