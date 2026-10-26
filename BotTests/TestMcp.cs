using Discord;
using HambotV2.Commands;
using HambotV2.Connections;
using HambotV2.Mcp;
using HambotV2.Watchdog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace BotTests
{
    /// <summary>
    /// The MCP surface: the endpoint's opt-in configuration, the
    /// bearer-key gate, each tool over fakes, and run_command's reply capture, tier-4
    /// authority, cooldown throttling, and audit semantics — without a live transport,
    /// database, or Discord connection.
    /// </summary>
    [TestClass]
    public sealed class TestMcp
    {
        private const ulong GuildId = 7;
        private const ulong GuildOwnerId = 999;
        private const ulong BotUserId = 900;

        // Configuration

        [TestMethod]
        public void Configuration_IsInactiveByDefaultAndWithoutAKey()
        {
            Assert.IsFalse(McpConfiguration.Parse(CreateConfiguration()).Enabled);
            Assert.IsFalse(McpConfiguration.Parse(CreateConfiguration()).IsActive);
            Assert.AreEqual(McpConfiguration.DefaultListenUri, McpConfiguration.Parse(CreateConfiguration()).ListenUri);

            // Enabled without a key keeps the endpoint down.
            McpConfiguration enabledWithoutKey = McpConfiguration.Parse(CreateConfiguration(("Mcp:Enabled", "true")));
            Assert.IsTrue(enabledWithoutKey.Enabled);
            Assert.IsFalse(enabledWithoutKey.IsActive);

            // Invalid values fall back to the default.
            Assert.IsFalse(McpConfiguration.Parse(CreateConfiguration(("Mcp:Enabled", "junk"))).Enabled);
        }

        [TestMethod]
        public void Configuration_ActiveOnlyWhenEnabledWithAKey()
        {
            McpConfiguration active = McpConfiguration.Parse(CreateConfiguration(
                ("Mcp:Enabled", "true"),
                ("Mcp:ApiKey", "odysseus-key")));

            Assert.IsTrue(active.IsActive);

            McpConfiguration customUri = McpConfiguration.Parse(CreateConfiguration(
                ("Mcp:Enabled", "true"),
                ("Mcp:ApiKey", "odysseus-key"),
                ("Mcp:ListenUri", " http://localhost:9999 ")));

            Assert.IsTrue(customUri.IsActive);
            Assert.AreEqual("http://localhost:9999", customUri.ListenUri, "The listen address is honored and trimmed.");
        }

        private static IConfiguration CreateConfiguration(params (string Key, string Value)[] entries)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(entries.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)))
                .Build();
        }

        // Bearer-key authentication

        [TestMethod]
        public void BearerKey_AcceptsOnlyTheConfiguredKey()
        {
            Assert.IsTrue(McpBearerAuthentication.IsValid("Bearer odysseus-key", "odysseus-key"));

            Assert.IsFalse(McpBearerAuthentication.IsValid(null, "odysseus-key"), "A missing header is rejected.");
            Assert.IsFalse(McpBearerAuthentication.IsValid("Bearer wrong-key", "odysseus-key"), "A wrong key is rejected.");
            Assert.IsFalse(McpBearerAuthentication.IsValid("odysseus-key", "odysseus-key"), "A bare key without the scheme is rejected.");
            Assert.IsFalse(McpBearerAuthentication.IsValid("bearer odysseus-key", "odysseus-key"), "The scheme is case-sensitive, like HTTP auth.");
        }

        // Tools over fakes

        [TestMethod]
        public void BotStatus_ReportsTheOperationalState()
        {
            var harness = new ToolHarness();
            harness.Discord.Guilds =
            [
                new DiscordGuildSummary(1, "one"),
                new DiscordGuildSummary(2, "two")
            ];
            harness.Discord.CurrentUser = CreateUser(BotUserId);
            harness.Presence.SetupGet(presence => presence.CurrentActivity)
                .Returns(new PresenceActivity(ActivityType.Playing, "with the wheel"));
            harness.Presence.SetupGet(presence => presence.CurrentBio).Returns("hamster");
            harness.Gateway.SetupGet(gateway => gateway.State).Returns("Connected");

            McpTools tools = harness.CreateTools();
            harness.Time.Advance(TimeSpan.FromHours(3));

            McpToolResult result = tools.BotStatus();

            Assert.IsFalse(result.IsError);
            StringAssert.Contains(result.Text, "Version: ");
            StringAssert.Contains(result.Text, "Uptime: 3h 0m");
            StringAssert.Contains(result.Text, "Gateway: Connected");
            StringAssert.Contains(result.Text, "Guilds: 2");
            StringAssert.Contains(result.Text, "Presence: playing with the wheel");
            StringAssert.Contains(result.Text, "Bio: hamster");
        }

        [TestMethod]
        public void ListGuilds_ListsIdAndNamePerGuild()
        {
            var harness = new ToolHarness();
            harness.Discord.Guilds = [new DiscordGuildSummary(42, "The Burrow")];

            McpToolResult result = harness.CreateTools().ListGuilds();

            Assert.IsFalse(result.IsError);
            Assert.AreEqual("42 The Burrow", result.Text);
        }

        [TestMethod]
        public void ListGuilds_NoGuildsReportsIt()
        {
            McpToolResult result = new ToolHarness().CreateTools().ListGuilds();

            Assert.IsFalse(result.IsError);
            StringAssert.Contains(result.Text, "not a member of any guild");
        }

        [TestMethod]
        public void ListCommands_ListsNameTierDescriptionAndUsage()
        {
            var harness = new ToolHarness();
            harness.CommandService.RegisterCommand("alpha", DiscordTestDoubles.CreateMockCommand("alpha", PermissionTier.Owner).Object);
            harness.CommandService.RegisterCommand("beta", DiscordTestDoubles.CreateMockCommand("beta").Object);

            McpToolResult result = harness.CreateTools().ListCommands();

            Assert.IsFalse(result.IsError);
            StringAssert.Contains(result.Text, "alpha (tier 3) — Test command alpha | usage: alpha");
            StringAssert.Contains(result.Text, "beta (tier 0) — Test command beta | usage: beta");
            Assert.IsTrue(result.Text.IndexOf("alpha", StringComparison.Ordinal) < result.Text.IndexOf("beta", StringComparison.Ordinal),
                "The commands are listed in name order.");
        }

        [TestMethod]
        public void ListCommands_RegistersTheBuiltInsLazily()
        {
            var harness = new ToolHarness();
            Assert.IsFalse(harness.CommandService.CommandsRegistered, "Precondition: nothing registered yet.");

            McpToolResult result = harness.CreateTools().ListCommands();

            Assert.IsFalse(result.IsError);
            Assert.IsTrue(harness.CommandService.CommandsRegistered, "The tool triggers the same lazy registration as the pipeline lookup.");
            StringAssert.Contains(result.Text, "help (tier 0)", "The built-in command surface is listed.");
        }

        [TestMethod]
        public async Task SetPresence_AppliesThroughThePresenceService()
        {
            var harness = new ToolHarness();
            harness.Presence
                .Setup(presence => presence.ApplyActivityAsync(ActivityType.Watching, "the stars"))
                .ReturnsAsync(true);

            McpToolResult result = await harness.CreateTools().SetPresenceAsync("watching", "the stars");

            Assert.IsFalse(result.IsError);
            StringAssert.Contains(result.Text, "Activity set: watching the stars");
            harness.Presence.Verify(presence => presence.ApplyActivityAsync(ActivityType.Watching, "the stars"), Times.Once);
        }

        [TestMethod]
        public async Task SetPresence_RejectsInvalidInputLikeThePresenceCommand()
        {
            var harness = new ToolHarness();

            McpToolResult unknownType = await harness.CreateTools().SetPresenceAsync("dancing", "anything");
            Assert.IsTrue(unknownType.IsError, "An unsupported activity type is rejected.");
            StringAssert.Contains(unknownType.Text, "supported types");

            McpToolResult multiline = await harness.CreateTools().SetPresenceAsync("playing", "one\ntwo");
            Assert.IsTrue(multiline.IsError, "A multi-line activity text is rejected.");

            McpToolResult empty = await harness.CreateTools().SetPresenceAsync("playing", "   ");
            Assert.IsTrue(empty.IsError, "An empty activity text is rejected.");

            McpToolResult tooLong = await harness.CreateTools().SetPresenceAsync("playing", new string('a', ProfileLimits.MaxActivityTextLength + 1));
            Assert.IsTrue(tooLong.IsError, "An over-limit activity text is rejected.");
            StringAssert.Contains(tooLong.Text, ProfileLimits.MaxActivityTextLength.ToString());

            harness.Presence.Verify(presence => presence.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task SetPresence_ReportsAFailedApplyAsAnErrorResult()
        {
            var harness = new ToolHarness();
            harness.Presence
                .Setup(presence => presence.ApplyActivityAsync(It.IsAny<ActivityType>(), It.IsAny<string>()))
                .ReturnsAsync(false);

            McpToolResult result = await harness.CreateTools().SetPresenceAsync("playing", "broken");

            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Text, "Failed to update the activity");
        }

        [TestMethod]
        public async Task SetBio_AppliesUnderTheLimitAndKeepsLineBreaks()
        {
            var harness = new ToolHarness();
            string bio = $"hello\n{new string('a', ProfileLimits.MaxBioLength - 6)}";
            harness.Presence.Setup(presence => presence.ApplyBioAsync(bio)).ReturnsAsync(true);

            McpToolResult result = await harness.CreateTools().SetBioAsync(bio);

            Assert.IsFalse(result.IsError);
            StringAssert.Contains(result.Text, "The bio was set");
            harness.Presence.Verify(presence => presence.ApplyBioAsync(bio), Times.Once);
        }

        [TestMethod]
        public async Task SetBio_RejectsEmptyAndOverLimitText()
        {
            var harness = new ToolHarness();

            McpToolResult empty = await harness.CreateTools().SetBioAsync("   ");
            Assert.IsTrue(empty.IsError, "An empty bio is rejected.");

            McpToolResult tooLong = await harness.CreateTools().SetBioAsync(new string('a', ProfileLimits.MaxBioLength + 1));
            Assert.IsTrue(tooLong.IsError, "An over-limit bio is rejected.");
            StringAssert.Contains(tooLong.Text, ProfileLimits.MaxBioLength.ToString());

            harness.Presence.Verify(presence => presence.ApplyBioAsync(It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task GetRecentMessages_ReturnsTheMessagesTheBotCanRead()
        {
            var harness = new ToolHarness();
            harness.Discord.RecentMessages =
            [
                new DiscordChannelMessage(42, "alice", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), "hello"),
                new DiscordChannelMessage(43, "bob", new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero), "hi")
            ];

            McpToolResult result = await harness.CreateTools().GetRecentMessagesAsync(123, 10);

            Assert.IsFalse(result.IsError);
            Assert.AreEqual(10, harness.Discord.RequestedLimit);
            StringAssert.Contains(result.Text, "alice (42): hello");
            StringAssert.Contains(result.Text, "bob (43): hi");
        }

        [TestMethod]
        public async Task GetRecentMessages_CapsTheLimitAt50()
        {
            var harness = new ToolHarness();
            harness.Discord.RecentMessages = [];

            McpToolResult over = await harness.CreateTools().GetRecentMessagesAsync(123, 500);
            Assert.IsFalse(over.IsError);
            Assert.AreEqual(McpTools.MaxRecentMessages, harness.Discord.RequestedLimit, "A limit above 50 is capped.");

            McpToolResult unset = await harness.CreateTools().GetRecentMessagesAsync(123, 0);
            Assert.IsFalse(unset.IsError);
            Assert.AreEqual(McpTools.MaxRecentMessages, harness.Discord.RequestedLimit, "A missing limit defaults to 50.");
        }

        [TestMethod]
        public async Task GetRecentMessages_ErrorsOnAnUnknownChannel()
        {
            var harness = new ToolHarness();
            harness.Discord.RecentMessages = null;

            McpToolResult result = await harness.CreateTools().GetRecentMessagesAsync(123, 10);

            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Text, "123");
        }

        [TestMethod]
        public async Task SendMessage_SendsThePlainText()
        {
            var harness = new ToolHarness();

            McpToolResult result = await harness.CreateTools().SendMessageAsync(55, "hello there");

            Assert.IsFalse(result.IsError);
            Assert.AreEqual(55UL, harness.Discord.SendTargetChannel);
            Assert.AreEqual("hello there", harness.Discord.SentText);
        }

        [TestMethod]
        public async Task SendMessage_RejectsEmptyAndOverLimitText()
        {
            var harness = new ToolHarness();

            McpToolResult empty = await harness.CreateTools().SendMessageAsync(55, "   ");
            Assert.IsTrue(empty.IsError, "An empty message is rejected.");

            McpToolResult tooLong = await harness.CreateTools().SendMessageAsync(55, new string('a', McpTools.MaxSendMessageLength + 1));
            Assert.IsTrue(tooLong.IsError, "A message over 2000 characters is rejected.");
            StringAssert.Contains(tooLong.Text, McpTools.MaxSendMessageLength.ToString());

            Assert.IsNull(harness.Discord.SentText, "Nothing is sent when the validation fails.");
        }

        [TestMethod]
        public async Task SendMessage_ErrorsOnAnUnknownChannel()
        {
            var harness = new ToolHarness();
            harness.Discord.SendResult = false;

            McpToolResult result = await harness.CreateTools().SendMessageAsync(55, "hello");

            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Text, "55");
        }

        [TestMethod]
        public async Task Tools_AFailedCallIsAnErrorResultNeverAnException()
        {
            var harness = new ToolHarness();
            harness.Discord.ThrowOnRecentMessages = true;

            McpToolResult result = await harness.CreateTools().GetRecentMessagesAsync(123, 10);

            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Text, "failed", "A failing call is returned as an error result.");
        }

        // run_command — virtual invocation through the real pipeline

        [TestMethod]
        public async Task RunCommand_CapturesTheReplyAndNeverPostsIt()
        {
            var pipeline = new McpPipeline();
            pipeline.CommandService.RegisterCommand("say", new ReplyCommand("hello from the pipeline"));

            var harness = new RunCommandHarness(pipeline);

            McpToolResult result = await harness.Tools.RunCommandAsync(GuildId, "say");

            Assert.IsFalse(result.IsError);
            Assert.AreEqual("hello from the pipeline", result.Text);
            Assert.AreEqual(0, harness.Channel.Sent.Count, "The command's reply is captured by the seam, never posted to the origin channel.");
        }

        [TestMethod]
        public async Task RunCommand_ActsAtTheGuildsGrantedTierWithoutADatabaseEntry()
        {
            var pipeline = new McpPipeline();
            pipeline.CommandService.RegisterCommand("owneronly", new ReplyCommand("owner-only reply", PermissionTier.BotOwner));

            // The guild granted the agent tier 4; the virtual user itself has no database entry.
            pipeline.GuildSettings.Setup(service => service.GetOperatorAgentTier(GuildId)).Returns(PermissionTier.BotOwner);

            var harness = new RunCommandHarness(pipeline);

            McpToolResult result = await harness.Tools.RunCommandAsync(GuildId, "owneronly");

            Assert.IsFalse(result.IsError, "The granted tier is what the agent runs tier-4 commands at, without any database entry.");
            StringAssert.Contains(result.Text, "owner-only reply");

            // The same user through a plain virtual invocation (no operator grant) is denied —
            // the tier-4 authority comes from the operator-agent override, not from the user id.
            VirtualCommandResult plain = await pipeline.CreateRunner().RunAsync(
                new VirtualCommandInvocation(harness.Discord.CurrentUser!, harness.Channel.Channel.Object, "owneronly", DateTimeOffset.UnixEpoch));

            Assert.AreEqual(CommandOutcome.DeniedPermission, plain.Outcome);
        }

        [TestMethod]
        public async Task RunCommand_WithoutAGrantTheAgentActsAtTheLowestTier()
        {
            var pipeline = new McpPipeline();
            pipeline.CommandService.RegisterCommand("modonly", new ReplyCommand("moderator reply", PermissionTier.Moderator));

            var harness = new RunCommandHarness(pipeline);

            McpToolResult result = await harness.Tools.RunCommandAsync(GuildId, "modonly");

            Assert.IsFalse(result.IsError, "A denial is a captured pipeline reply, not a tool failure.");
            StringAssert.Contains(result.Text, "You are not permitted to use that command", "Without a per-guild grant the agent runs only what everyone can.");
        }

        [TestMethod]
        public async Task RunCommand_CooldownsKeyOnTheBotsOwnUserAndGuild()
        {
            var pipeline = new McpPipeline();
            pipeline.CommandService.RegisterCommand("say", new ReplyCommand("reply"));

            ulong? cooldownGuild = null;
            ulong? cooldownUser = null;
            string? cooldownCommand = null;
            pipeline.Cooldown
                .Setup(cooldown => cooldown.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                .Returns((ulong? guildId, ulong userId, string commandName, int tier, IDatabaseContext database) =>
                {
                    cooldownGuild = guildId;
                    cooldownUser = userId;
                    cooldownCommand = commandName;
                    return CooldownCheck.Allowed;
                });

            var harness = new RunCommandHarness(pipeline);
            McpToolResult result = await harness.Tools.RunCommandAsync(GuildId, "say");

            Assert.IsFalse(result.IsError);
            Assert.AreEqual(GuildId.ToString(), cooldownGuild?.ToString(), "The cooldown keys on the invocation's guild.");
            Assert.AreEqual(BotUserId.ToString(), cooldownUser?.ToString(), "The cooldown keys on the bot's own user, so tool-call floods throttle.");
            Assert.AreEqual("say", cooldownCommand);
        }

        [TestMethod]
        public async Task RunCommand_AThrottleIsTheCapturedPipelineReply()
        {
            var pipeline = new McpPipeline();
            pipeline.CommandService.RegisterCommand("say", new ReplyCommand("reply"));
            pipeline.Cooldown
                .Setup(cooldown => cooldown.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                .Returns(new CooldownCheck(IsAllowed: false, RemainingWait: TimeSpan.FromSeconds(3)));

            var harness = new RunCommandHarness(pipeline);

            McpToolResult result = await harness.Tools.RunCommandAsync(GuildId, "say");

            Assert.IsFalse(result.IsError, "A throttle is a captured pipeline reply, not a tool failure.");
            StringAssert.Contains(result.Text, "too quickly");
        }

        [TestMethod]
        public async Task RunCommand_FiresThePipelineAuditEvent()
        {
            var pipeline = new McpPipeline();
            pipeline.CommandService.RegisterCommand("say", new ReplyCommand("reply"));

            var harness = new RunCommandHarness(pipeline);
            await harness.Tools.RunCommandAsync(GuildId, "say");

            (LogLevel Level, IReadOnlyList<KeyValuePair<string, string?>> Values) audit = pipeline.HandlerLog.Entries
                .Single(entry => entry.Values.Any(pair => pair.Key == "CommandName" && pair.Value == "say"));

            Assert.AreEqual(LogLevel.Information, audit.Level, "The standard command audit event fires for the virtual invocation.");
            Assert.IsTrue(audit.Values.Any(pair => pair.Key == "Outcome" && pair.Value == CommandOutcome.Executed.ToString()));
            Assert.IsFalse(audit.Values.Any(pair => pair.Key == "Content"), "The audit event carries no message content.");
        }

        [TestMethod]
        public async Task RunCommand_ThePerGuildTierGovernsWhatTheAgentMayRun()
        {
            var pipeline = new McpPipeline();
            pipeline.CommandService.RegisterCommand("modonly", new ReplyCommand("moderator reply", PermissionTier.Moderator));
            pipeline.CommandService.RegisterCommand("adminonly", new ReplyCommand("admin reply", PermissionTier.Admin));
            pipeline.GuildSettings.Setup(service => service.GetOperatorAgentTier(GuildId)).Returns(PermissionTier.Moderator);

            var harness = new RunCommandHarness(pipeline);

            McpToolResult allowed = await harness.Tools.RunCommandAsync(GuildId, "modonly");
            Assert.IsFalse(allowed.IsError, "A command at or below the guild's granted tier still runs.");
            StringAssert.Contains(allowed.Text, "moderator reply");

            McpToolResult denied = await harness.Tools.RunCommandAsync(GuildId, "adminonly");
            Assert.IsFalse(denied.IsError, "A denial is a captured pipeline reply, not a tool failure.");
            StringAssert.Contains(denied.Text, "You are not permitted to use that command", "A command above the guild's granted tier is denied like any other invocation.");
        }

        [TestMethod]
        public async Task RunCommand_ErrorsOnInvalidInput()
        {
            var pipeline = new McpPipeline();
            pipeline.CommandService.RegisterCommand("say", new ReplyCommand("reply"));

            var harness = new RunCommandHarness(pipeline);

            McpToolResult unknownGuild = await harness.Tools.RunCommandAsync(GuildId + 1, "say");
            Assert.IsTrue(unknownGuild.IsError, "An unknown guild is an error result.");
            StringAssert.Contains(unknownGuild.Text, "no accessible text channel");

            harness.Discord.CurrentUser = null;
            McpToolResult noUser = await harness.Tools.RunCommandAsync(GuildId, "say");
            Assert.IsTrue(noUser.IsError, "A missing bot user (gateway still connecting) is an error result.");

            harness.Discord.CurrentUser = CreateUser(BotUserId);
            McpToolResult empty = await harness.Tools.RunCommandAsync(GuildId, "   ");
            Assert.IsTrue(empty.IsError, "An empty command line is an error result.");
        }

        private static IUser CreateUser(ulong discordId)
        {
            var user = new Mock<IUser>();
            user.SetupGet(candidate => candidate.Id).Returns(discordId);
            return user.Object;
        }

        /// <summary>The fakes behind the non-command tools: everything injected into <see cref="McpTools"/> except the runner.</summary>
        private sealed class ToolHarness
        {
            public FakeMcpDiscordClient Discord { get; } = new();
            public Mock<IPresenceService> Presence { get; } = new();
            public Mock<IGatewayStatus> Gateway { get; } = new();
            public CommandService CommandService { get; } = new();
            public FakeTimeProvider Time { get; } = new(DateTimeOffset.UnixEpoch);

            public McpTools CreateTools()
            {
                return new McpTools(
                    Discord, Presence.Object, Gateway.Object, CommandService,
                    Mock.Of<IVirtualCommandRunner>(), Time, NullLogger<McpTools>.Instance);
            }
        }

        private sealed class FakeMcpDiscordClient : IMcpDiscordClient
        {
            public IUser? CurrentUser { get; set; }
            public IReadOnlyList<DiscordGuildSummary> Guilds { get; set; } = [];
            public IReadOnlyList<DiscordChannelMessage>? RecentMessages { get; set; } = [];
            public bool SendResult { get; set; } = true;
            public bool ThrowOnRecentMessages { get; set; }

            /// <summary>The guild whose text channel resolves; other guild ids return null like the cache would.</summary>
            public ulong KnownGuildId { get; set; } = GuildId;

            public int RequestedLimit { get; private set; }
            public ulong? SendTargetChannel { get; private set; }
            public string? SentText { get; private set; }

            public IMessageChannel? Channel { get; set; }

            public IReadOnlyList<DiscordGuildSummary> GetGuilds() => Guilds;

            public IMessageChannel? GetGuildTextChannel(ulong guildId) => guildId == KnownGuildId ? Channel : null;

            public async Task<IReadOnlyList<DiscordChannelMessage>?> GetRecentMessagesAsync(ulong channelId, int limit)
            {
                RequestedLimit = limit;
                if (ThrowOnRecentMessages)
                    await Task.FromException(new InvalidOperationException("Discord is down"));
                return RecentMessages;
            }

            public Task<bool> TrySendMessageAsync(ulong channelId, string text)
            {
                SendTargetChannel = channelId;
                SentText = text;
                return Task.FromResult(SendResult);
            }
        }

        /// <summary>The real command pipeline behind run_command, like the production container wires per invocation.</summary>
        private sealed class McpPipeline
        {
            public Mock<IGuildSettingsService> GuildSettings { get; } = new();
            public Mock<ICooldownService> Cooldown { get; } = new();
            public CommandService CommandService { get; } = new();
            public FakeTimeProvider Time { get; } = new(DateTimeOffset.UnixEpoch);
            public RecordingLogger<CommandHandler> HandlerLog { get; } = new();
            public string DatabaseStoreName { get; } = Guid.NewGuid().ToString();

            public McpPipeline()
            {
                GuildSettings.Setup(service => service.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");
                GuildSettings.SetupGet(service => service.GlobalCommandIdentifier).Returns("!");
                // The operator agent's per-guild tier: the lowest tier unless the guild granted more.
                GuildSettings.Setup(service => service.GetOperatorAgentTier(It.IsAny<ulong>())).Returns(PermissionTier.Everyone);
                Cooldown
                    .Setup(cooldown => cooldown.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                    .Returns(CooldownCheck.Allowed);
            }

            /// <summary>One runner per invocation, resolving a transient handler over a fresh context, like the production container.</summary>
            public VirtualCommandRunner CreateRunner()
            {
                var database = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>()
                    .UseInMemoryDatabase(DatabaseStoreName)
                    .Options);
                var handler = new CommandHandler(
                    CommandService, GuildSettings.Object, new PermissionsService(), Cooldown.Object,
                    database, new ConfigurationBuilder().Build(), HandlerLog);
                var provider = Mock.Of<IServiceProvider>(serviceProvider => serviceProvider.GetService(typeof(ICommandHandler)) == handler);
                return new VirtualCommandRunner(GuildSettings.Object, provider);
            }

            public McpTools CreateTools(IMcpDiscordClient discord)
            {
                return new McpTools(
                    discord, Mock.Of<IPresenceService>(), Mock.Of<IGatewayStatus>(), CommandService,
                    CreateRunner(), Time, NullLogger<McpTools>.Instance);
            }
        }

        private sealed class RunCommandHarness
        {
            private readonly McpPipeline _pipeline;
            public FakeMcpDiscordClient Discord { get; }
            public RecordingGuildChannel Channel { get; }

            /// <summary>One fresh tool/runner per call, so every invocation resolves its own transient handler and context — like the production container.</summary>
            public McpTools Tools => _pipeline.CreateTools(Discord);

            public RunCommandHarness(McpPipeline pipeline)
            {
                _pipeline = pipeline;
                Channel = new RecordingGuildChannel(GuildId, GuildOwnerId);
                Discord = new FakeMcpDiscordClient
                {
                    CurrentUser = CreateUser(BotUserId),
                    Channel = Channel.Channel.Object
                };
            }
        }

        /// <summary>A guild text channel double whose sends are recorded — proving the seam never posts to the origin.</summary>
        private sealed class RecordingGuildChannel
        {
            public Mock<ITextChannel> Channel { get; } = new();
            public List<string> Sent { get; } = [];

            public RecordingGuildChannel(ulong guildId, ulong ownerId)
            {
                var guild = new Mock<IGuild>();
                guild.SetupGet(candidate => candidate.OwnerId).Returns(ownerId);

                Channel.SetupGet(candidate => candidate.GuildId).Returns(guildId);
                Channel.SetupGet(candidate => candidate.Guild).Returns(guild.Object);
                Channel.SetupGet(candidate => candidate.Id).Returns(55UL);
                Channel
                    .Setup(candidate => candidate.SendMessageAsync(
                        It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Embed?>(), It.IsAny<RequestOptions?>(),
                        It.IsAny<AllowedMentions?>(), It.IsAny<MessageReference?>(), It.IsAny<MessageComponent?>(),
                        It.IsAny<ISticker[]?>(), It.IsAny<Embed[]?>(), It.IsAny<MessageFlags>(), It.IsAny<PollProperties?>()))
                    .Callback<string, bool, Embed?, RequestOptions?, AllowedMentions?, MessageReference?, MessageComponent?, ISticker[]?, Embed[]?, MessageFlags, PollProperties?>(
                        (text, _, _, _, _, _, _, _, _, _, _) => Sent.Add(text))
                    .Returns(Task.FromResult<IUserMessage?>(null));
            }
        }

        private sealed class RecordingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, IReadOnlyList<KeyValuePair<string, string?>> Values)> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    lock (Entries)
                    {
                        Entries.Add((logLevel, values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value?.ToString())).ToList()));
                    }
                }
            }
        }

        /// <summary>A command replying with a fixed text; its tier is configurable for the authority tests.</summary>
        private sealed class ReplyCommand : ICommand
        {
            private readonly string _reply;

            public ReplyCommand(string reply, int defaultPermissionTier = PermissionTier.Everyone)
            {
                _reply = reply;
                DefaultPermissionTier = defaultPermissionTier;
            }

            public string Name => "say";
            public string Description => "Replies with a fixed text for testing";
            public string Usage => "say";
            public int DefaultPermissionTier { get; }

            public Task ExecuteCommand(IMessage message, ICommandContext context)
                => message.Channel.SendMessageAsync(_reply);
        }
    }
}
