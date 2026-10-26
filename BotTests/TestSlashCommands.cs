using Discord;
using HambotV2.Commands;
using HambotV2.Connections;
using HambotV2.SlashCommands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>
    /// The slash surface (design §3.15): the interaction→command-line
    /// mapping, the virtual invocation through the real pipeline (shared tiers, cooldowns,
    /// audit outcomes, DM scope), and the registration contract (every slash-eligible command
    /// yields a valid definition, ineligible ones none).
    /// </summary>
    [TestClass]
    public sealed class TestSlashCommands
    {
        private const ulong GuildId = 1;
        private const ulong GuildOwnerId = 999;
        private const ulong InvokerId = 123456789;

        private static readonly string[] ExpectedSlashEligible =
        [
            "backup", "backups", "bio", "cancelreminder", "commandpermission", "health",
            "help", "notifychannel", "presence", "remindme", "reminders", "restorebackup",
            "settings", "userpermission", "avatar", "banner",
        ];

        private static readonly string[] ExpectedTextOnly =
        [
            "addresponse", "hello", "importquotes", "listresponses", "quote",
            "removeresponse", "removequote", "searchquotes", "version",
        ];

        private static CommandService CreateRegisteredCommandService()
        {
            var commandService = new CommandService();
            commandService.RegisterCommands();
            return commandService;
        }

        // Registration contract

        [TestMethod]
        public void RegistrationContract_EveryEligibleCommandYieldsAValidDefinition()
        {
            CommandService commandService = CreateRegisteredCommandService();

            var eligible = new HashSet<string>(ExpectedSlashEligible, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach ((string name, ICommand command) in commandService.Commands)
            {
                if (command.ApplicationCommand is null)
                    continue;

                Assert.IsTrue(TryCreate(command, out ApplicationCommandDefinition? definition, out string? error), $"'{name}' failed the definition contract: {error}");
                Assert.AreEqual(name, definition!.Name, "The slash name must be the registry key.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(definition.Description), $"'{name}' has no slash description.");
                Assert.IsTrue(definition.Description.Length <= 100, $"'{name}' description exceeds Discord's 100-character limit.");
                Assert.IsTrue(definition.Options.Count <= 25, $"'{name}' exceeds Discord's option limit.");

                foreach (ApplicationCommandOption option in definition.Options)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(option.Name), $"'{name}' has an option without a name.");
                    Assert.AreEqual(option.Name.ToLowerInvariant(), option.Name, $"'{name}' option '{option.Name}' must be lowercase.");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(option.Description), $"'{name}' option '{option.Name}' has no description.");
                    Assert.IsTrue(option.Description.Length <= 100, $"'{name}' option '{option.Name}' description exceeds 100 characters.");
                }

                // Discord rejects required options that come after optional ones.
                bool seenOptional = false;
                foreach (ApplicationCommandOption option in definition.Options)
                {
                    Assert.IsFalse(option.Required && seenOptional, $"'{name}' declares a required option after an optional one.");
                    seenOptional |= !option.Required;
                }

                seen.Add(name);
            }

            CollectionAssert.AreEquivalent((System.Collections.ICollection)eligible.OrderBy(name => name).ToList(), (System.Collections.ICollection)seen.OrderBy(name => name).ToList(), "The slash-eligible set must be exactly the administrative/configuration and user-scoped commands.");
        }

        [TestMethod]
        public void RegistrationContract_CommunityFacingCommandsStayTextOnly()
        {
            CommandService commandService = CreateRegisteredCommandService();

            foreach (string name in ExpectedTextOnly)
            {
                Assert.IsTrue(commandService.Commands.TryGetValue(name, out ICommand? command), $"Command '{name}' should be registered.");
                Assert.IsNull(command!.ApplicationCommand, $"Community-facing command '{name}' must stay text-only.");
            }
        }

        [TestMethod]
        public void RegistrationContract_TypedCommandsDeclareTheirOptions()
        {
            CommandService commandService = CreateRegisteredCommandService();

            var userPermission = (ICommand)commandService.Commands["userpermission"];
            Assert.IsNotNull(userPermission.ApplicationCommand);
            CollectionAssert.AreEqual(new[] { "user", "tier" }, userPermission.ApplicationCommand!.Options.Select(option => option.Name).ToArray());
            Assert.AreEqual(ApplicationCommandOptionKind.User, userPermission.ApplicationCommand.Options[0].Kind);
            Assert.IsTrue(userPermission.ApplicationCommand.Options[0].Required);
            Assert.AreEqual(ApplicationCommandOptionKind.Integer, userPermission.ApplicationCommand.Options[1].Kind);
            Assert.IsFalse(userPermission.ApplicationCommand.Options[1].Required);

            var cancelReminder = (ICommand)commandService.Commands["cancelreminder"];
            Assert.IsNotNull(cancelReminder.ApplicationCommand);
            ApplicationCommandOption number = cancelReminder.ApplicationCommand!.Options.Single();
            Assert.AreEqual("number", number.Name);
            Assert.AreEqual(ApplicationCommandOptionKind.Integer, number.Kind);
            Assert.IsTrue(number.Required);

            var reminders = (ICommand)commandService.Commands["reminders"];
            Assert.IsNotNull(reminders.ApplicationCommand);
            Assert.AreEqual(0, reminders.ApplicationCommand!.Options.Count, "A no-argument command registers without options.");

            // The free-form fallback: one optional 'arguments' string option.
            var remindMe = (ICommand)commandService.Commands["remindme"];
            ApplicationCommandOption freeForm = remindMe.ApplicationCommand!.Options.Single();
            Assert.AreEqual(ApplicationCommandOption.FreeFormOptionName, freeForm.Name);
            Assert.IsFalse(freeForm.Required);

            var help = (ICommand)commandService.Commands["help"];
            Assert.IsNotNull(help.ApplicationCommand);
            CollectionAssert.AreEqual(new[] { "command", "page" }, help.ApplicationCommand!.Options.Select(option => option.Name).ToArray());
            Assert.AreEqual(ApplicationCommandOptionKind.Text, help.ApplicationCommand.Options[0].Kind);
            Assert.AreEqual(ApplicationCommandOptionKind.Integer, help.ApplicationCommand.Options[1].Kind);
            Assert.IsFalse(help.ApplicationCommand.Options[0].Required);
            Assert.IsFalse(help.ApplicationCommand.Options[1].Required);
        }

        [TestMethod]
        public void RegistrationContract_InvalidDeclarationsAreRejected()
        {
            var broken = new StubCommand("broken", new ApplicationCommandDeclaration([new ApplicationCommandOption("option", "ok", ApplicationCommandOptionKind.Text)]))
            {
                Description = new string('x', 101)
            };
            Assert.IsFalse(TryCreate(broken, out _, out string? error));
            StringAssert.Contains(error, "broken");

            var badOrder = new StubCommand("badorder", new ApplicationCommandDeclaration(
            [
                new ApplicationCommandOption("optional", "optional", ApplicationCommandOptionKind.Text, Required: false),
                new ApplicationCommandOption("required", "required", ApplicationCommandOptionKind.Text),
            ]));
            Assert.IsFalse(TryCreate(badOrder, out _, out string? orderError));
            StringAssert.Contains(orderError, "before optional ones");

            var badOptionName = new StubCommand("badoption", new ApplicationCommandDeclaration(
            [
                new ApplicationCommandOption("NotLowercase", "an option", ApplicationCommandOptionKind.Text),
            ]));
            Assert.IsFalse(TryCreate(badOptionName, out _, out string? nameError));
            StringAssert.Contains(nameError, "NotLowercase");
        }

        private static bool TryCreate(ICommand command, out ApplicationCommandDefinition? definition, out string? error)
        {
            return ApplicationCommandDefinitionFactory.TryCreate(command, out definition, out error);
        }

        // Interaction → command-line mapping

        [TestMethod]
        public void CommandLine_TypedOptionsJoinInDeclarationOrder()
        {
            CommandService commandService = CreateRegisteredCommandService();
            ICommand userPermission = commandService.Commands["userpermission"];
            ApplicationCommandDeclaration declaration = userPermission.ApplicationCommand!;

            var values = new Dictionary<string, object?>
            {
                // Deliberately out of declaration order: the mapping follows the declaration.
                ["tier"] = 2L,
                ["user"] = 42UL,
            };

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("userpermission", declaration, values, out string commandLine));
            Assert.AreEqual("userpermission <@42> 2", commandLine);
        }

        [TestMethod]
        public void CommandLine_OptionalOptionMayBeAbsent()
        {
            CommandService commandService = CreateRegisteredCommandService();
            ApplicationCommandDeclaration declaration = commandService.Commands["userpermission"].ApplicationCommand!;

            var values = new Dictionary<string, object?> { ["user"] = 42UL };

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("userpermission", declaration, values, out string commandLine));
            Assert.AreEqual("userpermission <@42>", commandLine);
        }

        [TestMethod]
        public void CommandLine_IntegerRendersInvariantly()
        {
            CommandService commandService = CreateRegisteredCommandService();
            ApplicationCommandDeclaration declaration = commandService.Commands["cancelreminder"].ApplicationCommand!;

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("cancelreminder", declaration, new Dictionary<string, object?> { ["number"] = 3L }, out string commandLine));
            Assert.AreEqual("cancelreminder 3", commandLine);
        }

        [TestMethod]
        public void CommandLine_FreeFormOptionIsTheWholeArgumentLine()
        {
            CommandService commandService = CreateRegisteredCommandService();
            ApplicationCommandDeclaration declaration = commandService.Commands["remindme"].ApplicationCommand!;

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("remindme", declaration, new Dictionary<string, object?> { ["arguments"] = "every 1d take vitamins" }, out string commandLine));
            Assert.AreEqual("remindme every 1d take vitamins", commandLine);
        }

        [TestMethod]
        public void CommandLine_NoOptionsIsTheBareCommandName()
        {
            CommandService commandService = CreateRegisteredCommandService();
            ApplicationCommandDeclaration declaration = commandService.Commands["reminders"].ApplicationCommand!;

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("reminders", declaration, new Dictionary<string, object?>(), out string commandLine));
            Assert.AreEqual("reminders", commandLine);
        }

        [TestMethod]
        public void CommandLine_UnmappableValueFailsWithoutExecutingAnything()
        {
            CommandService commandService = CreateRegisteredCommandService();
            ApplicationCommandDeclaration declaration = commandService.Commands["userpermission"].ApplicationCommand!;

            // A user option whose value is not a snowflake cannot become a text-surface mention.
            Assert.IsFalse(SlashCommandLineBuilder.TryBuild("userpermission", declaration, new Dictionary<string, object?> { ["user"] = "not-a-user" }, out _));
        }

        [TestMethod]
        public void CommandLine_ChannelOptionRendersChannelMention()
        {
            var declaration = new ApplicationCommandDeclaration(
            [
                new ApplicationCommandOption("channel", "The notification channel", ApplicationCommandOptionKind.Channel),
            ]);

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("notifychannel", declaration, new Dictionary<string, object?> { ["channel"] = 77UL }, out string commandLine));
            Assert.AreEqual("notifychannel <#77>", commandLine);
        }

        [TestMethod]
        public void CommandLine_HelpOptionsMapToTheirTextArguments()
        {
            CommandService commandService = CreateRegisteredCommandService();
            ApplicationCommandDeclaration declaration = commandService.Commands["help"].ApplicationCommand!;

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("help", declaration, new Dictionary<string, object?> { ["command"] = "quote" }, out string detailLine));
            Assert.AreEqual("help quote", detailLine);

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("help", declaration, new Dictionary<string, object?> { ["page"] = 2L }, out string pageLine));
            Assert.AreEqual("help 2", pageLine);

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("help", declaration, new Dictionary<string, object?> { ["command"] = "quote", ["page"] = 3L }, out string bothLine));
            Assert.AreEqual("help quote 3", bothLine);

            Assert.IsTrue(SlashCommandLineBuilder.TryBuild("help", declaration, new Dictionary<string, object?>(), out string bareLine));
            Assert.AreEqual("help", bareLine);
        }

        // Virtual invocation through the real pipeline

        /// <summary>Wires a real CommandHandler behind a VirtualCommandRunner, like the production container does per invocation.</summary>
        private sealed class VirtualPipeline
        {
            public Mock<IPermissionsService> Permissions { get; } = new();
            public Mock<ICooldownService> Cooldown { get; } = new();
            public Mock<IGuildSettingsService> GuildSettings { get; } = new();
            public string DatabaseStoreName { get; } = Guid.NewGuid().ToString();
            public RecordingDatabase Database { get; }
            public CommandService CommandService { get; }

            /// <summary>When set, the pipeline grants that user the tier-4 bot-owner override — the help pagination test needs the full tier-4 command list to span multiple pages.</summary>
            public string? BotOwnerId { get; set; }

            public VirtualPipeline()
            {
                Database = new RecordingDatabase(DatabaseStoreName);
                CommandService = new CommandService();

                GuildSettings.Setup(service => service.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");
                GuildSettings.SetupGet(service => service.GlobalCommandIdentifier).Returns("!");

                Cooldown
                    .Setup(cooldown => cooldown.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                    .Returns(CooldownCheck.Allowed);

                Permissions
                    .Setup(p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()))
                    .ReturnsAsync(new PermissionCheckResult(IsAllowed: true, UserEffectiveTier: PermissionTier.Owner));
            }

            /// <summary>The command handler disposes its per-invocation context; the in-memory store survives for read-backs.</summary>
            public DatabaseContext CreateDatabaseReader()
            {
                return new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>()
                    .UseInMemoryDatabase(DatabaseStoreName)
                    .Options);
            }

            public VirtualCommandRunner CreateRunner()
            {
                IConfiguration configuration = BotOwnerId is null
                    ? Mock.Of<IConfiguration>()
                    : new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?> { ["Permissions:BotOwnerDiscordId"] = BotOwnerId })
                        .Build();
                var handler = new CommandHandler(CommandService, GuildSettings.Object, Permissions.Object, Cooldown.Object, Database, configuration, Mock.Of<ILogger<CommandHandler>>());
                var provider = Mock.Of<IServiceProvider>(sp => sp.GetService(typeof(ICommandHandler)) == handler);
                return new VirtualCommandRunner(GuildSettings.Object, provider);
            }

            /// <summary>A DM origin channel: not a guild channel, so the pipeline runs in the DM scope.</summary>
            public IMessageChannel CreateDirectMessageChannel()
            {
                return new Mock<IMessageChannel>().Object;
            }

            /// <summary>A guild origin channel: carries the guild id and its owner, like a real text channel.</summary>
            public IMessageChannel CreateGuildChannel(ulong ownerId = GuildOwnerId)
            {
                var guild = new Mock<IGuild>();
                guild.SetupGet(g => g.OwnerId).Returns(ownerId);

                var channel = new Mock<ITextChannel>();
                channel.SetupGet(c => c.GuildId).Returns(GuildId);
                channel.SetupGet(c => c.Guild).Returns(guild.Object);
                channel.SetupGet(c => c.Id).Returns(55UL);
                return channel.Object;
            }
        }

        private sealed class RecordingDatabase : DatabaseContext
        {
            public RecordingDatabase(string storeName) : base(new DbContextOptionsBuilder<DatabaseContext>()
                .UseInMemoryDatabase(storeName)
                .Options) { }
        }

        private static Task<VirtualCommandResult> RunAsync(VirtualPipeline pipeline, IMessageChannel channel, string commandLine)
        {
            var author = new Mock<IUser>();
            author.SetupGet(u => u.Id).Returns(InvokerId);
            return pipeline.CreateRunner().RunAsync(new VirtualCommandInvocation(author.Object, channel, commandLine, DateTimeOffset.UnixEpoch));
        }

        [TestMethod]
        public async Task VirtualInvocation_ExecutesTheCommandAndCapturesItsReply()
        {
            var pipeline = new VirtualPipeline();

            VirtualCommandResult result = await RunAsync(pipeline, pipeline.CreateGuildChannel(), "reminders");

            Assert.AreEqual(CommandOutcome.Executed, result.Outcome);
            Assert.AreEqual(1, result.Replies.Count, "The command's reply is captured, not posted.");
            StringAssert.Contains(result.Replies[0].Text, "reminder");
        }

        [TestMethod]
        public async Task VirtualInvocation_HelpPageCarriesItsPaginationButtons()
        {
            // The invoker acts as the configured bot owner, so the list spans multiple pages —
            // the interaction reply re-posts the captured buttons, and a click re-enters this seam.
            var pipeline = new VirtualPipeline { BotOwnerId = InvokerId.ToString() };

            VirtualCommandResult result = await RunAsync(pipeline, pipeline.CreateGuildChannel(), "help 2");

            Assert.AreEqual(CommandOutcome.Executed, result.Outcome);
            StringAssert.Contains(result.Replies[0].Text, "page 2 of");
            Assert.IsNotNull(result.Replies[0].Components, "A multi-page help list carries its pagination buttons through the seam.");
        }

        [TestMethod]
        public async Task VirtualInvocation_UsesTheGuildsCommandIdentifier()
        {
            var pipeline = new VirtualPipeline();
            pipeline.GuildSettings.Setup(service => service.GetCommandIdentifier(GuildId)).Returns("?");

            // The runner prefixes the guild's effective identifier, so the command line still
            // resolves — a text message with that identifier would work the same way.
            VirtualCommandResult result = await RunAsync(pipeline, pipeline.CreateGuildChannel(), "reminders");

            Assert.AreEqual(CommandOutcome.Executed, result.Outcome);
        }

        [TestMethod]
        public async Task VirtualInvocation_SharedPermissionGate_DenialsAreReportedLikeText()
        {
            var pipeline = new VirtualPipeline();
            pipeline.Permissions
                .Setup(p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()))
                .ReturnsAsync(new PermissionCheckResult(IsAllowed: false, UserEffectiveTier: PermissionTier.Everyone));

            VirtualCommandResult result = await RunAsync(pipeline, pipeline.CreateGuildChannel(), "userpermission <@42> 1");

            Assert.AreEqual(CommandOutcome.DeniedPermission, result.Outcome);
            CollectionAssert.AreEqual(new[] { "You are not permitted to use that command" }, (System.Collections.ICollection)result.Replies.Select(reply => reply.Text).ToList());
        }

        [TestMethod]
        public async Task VirtualInvocation_CooldownKeySharesUserGuildAndCommandName()
        {
            var pipeline = new VirtualPipeline();
            string? cooldownUser = null, cooldownCommand = null;
            ulong? cooldownGuild = null;
            pipeline.Cooldown
                .Setup(cooldown => cooldown.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                .Returns((ulong? guildId, ulong userId, string name, int tier, IDatabaseContext database) =>
                {
                    cooldownGuild = guildId;
                    cooldownUser = userId.ToString();
                    cooldownCommand = name;
                    return CooldownCheck.Allowed;
                });

            VirtualCommandResult result = await RunAsync(pipeline, pipeline.CreateGuildChannel(), "reminders");

            Assert.AreEqual(CommandOutcome.Executed, result.Outcome);
            Assert.AreEqual(GuildId.ToString(), cooldownGuild?.ToString());
            Assert.AreEqual(InvokerId.ToString(), cooldownUser);
            Assert.AreEqual("reminders", cooldownCommand);
        }

        [TestMethod]
        public async Task VirtualInvocation_ThrottledReportsTheSharedThrottleReply()
        {
            var pipeline = new VirtualPipeline();
            pipeline.Cooldown
                .Setup(cooldown => cooldown.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                .Returns(new CooldownCheck(IsAllowed: false, RemainingWait: TimeSpan.FromSeconds(3)));

            VirtualCommandResult result = await RunAsync(pipeline, pipeline.CreateGuildChannel(), "reminders");

            Assert.AreEqual(CommandOutcome.Throttled, result.Outcome);
            Assert.AreEqual(1, result.Replies.Count);
            StringAssert.Contains(result.Replies[0].Text, "too quickly");
        }

        [TestMethod]
        public async Task VirtualInvocation_DirectMessageRunsInTheDmScope()
        {
            var pipeline = new VirtualPipeline();

            // A DM origin channel is not a guild channel, so userpermission hits its guild guard
            // : the DM scope reply is captured exactly as for a text DM.
            VirtualCommandResult result = await RunAsync(pipeline, pipeline.CreateDirectMessageChannel(), "userpermission <@42> 1");

            Assert.AreEqual(CommandOutcome.Executed, result.Outcome);
            Assert.AreEqual(1, result.Replies.Count);
            StringAssert.Contains(result.Replies[0].Text, "only available in servers");
        }

        [TestMethod]
        public async Task VirtualInvocation_CommandThrowsReturnsTheGenericErrorOutcome()
        {
            var pipeline = new VirtualPipeline();
            pipeline.CommandService.RegisterCommand("kaboom", new ThrowingCommand());

            VirtualCommandResult result = await RunAsync(pipeline, pipeline.CreateGuildChannel(), "kaboom");

            Assert.AreEqual(CommandOutcome.Failed, result.Outcome);
            Assert.AreEqual(1, result.Replies.Count);
            StringAssert.Contains(result.Replies[0].Text, "unexpected error");
        }

        private sealed class ThrowingCommand : ICommand
        {
            public string Name => "kaboom";
            public string Description => "Throws for testing the failure path";
            public string Usage => "kaboom";
            public int DefaultPermissionTier => PermissionTier.Everyone;

            public Task ExecuteCommand(IMessage message, ICommandContext context)
                => throw new InvalidOperationException("kaboom");
        }

        [TestMethod]
        public async Task VirtualInvocation_MappedTypedInvocationBehavesLikeItsTextTwin()
        {
            var pipeline = new VirtualPipeline();

            // The invoker acts as the guild owner (the tier escalation guard needs a tier above
            // the target's), exactly like a real admin would.
            IMessageChannel guildChannel = pipeline.CreateGuildChannel(ownerId: InvokerId);

            // The mapped userpermission invocation from CommandLine_TypedOptionsJoinInDeclarationOrder,
            // executed against a real database: the mention parses, the tier applies.
            VirtualCommandResult slashResult = await RunAsync(pipeline, guildChannel, "userpermission <@42> 1");
            Assert.AreEqual(CommandOutcome.Executed, slashResult.Outcome);
            StringAssert.Contains(slashResult.Replies[0].Text, "Updated permission tier for user <@42> to 1");

            var secondPipeline = new VirtualPipeline();
            VirtualCommandResult textResult = await RunAsync(secondPipeline, secondPipeline.CreateGuildChannel(ownerId: InvokerId), "userpermission <@42> 1");
            Assert.AreEqual(slashResult.Outcome, textResult.Outcome);
            Assert.AreEqual(slashResult.Replies[0].Text, textResult.Replies[0].Text);
        }

        [TestMethod]
        public async Task VirtualInvocation_TimestampFlowsIntoScheduling()
        {
            var pipeline = new VirtualPipeline();
            var author = new Mock<IUser>();
            author.SetupGet(u => u.Id).Returns(InvokerId);

            DateTimeOffset invocationTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            VirtualCommandResult result = await pipeline.CreateRunner().RunAsync(
                new VirtualCommandInvocation(author.Object, pipeline.CreateGuildChannel(), "remindme 1h water the plants", invocationTime));

            Assert.AreEqual(CommandOutcome.Executed, result.Outcome);

            // The handler's per-invocation context is disposed after the run; the in-memory
            // store survives, so the reminder is read back through a fresh context.
            using var reader = pipeline.CreateDatabaseReader();
            DatabaseContext.Reminder reminder = reader.Reminders.Single();
            Assert.AreEqual(invocationTime.UtcDateTime.AddHours(1), reminder.DueAtUtc, "The reminder is scheduled from the invocation timestamp.");
        }

        // Configuration

        [TestMethod]
        public void Enabled_IsOffByDefaultAndForInvalidValues()
        {
            Assert.IsFalse(CreateServiceEnabled(null).Enabled);
            Assert.IsFalse(CreateServiceEnabled("").Enabled);
            Assert.IsFalse(CreateServiceEnabled("not-a-bool").Enabled);
            Assert.IsFalse(CreateServiceEnabled("false").Enabled);
            Assert.IsTrue(CreateServiceEnabled("true").Enabled);
            Assert.IsTrue(CreateServiceEnabled("True").Enabled);
        }

        private static SlashCommandService CreateServiceEnabled(string? configuredValue)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["SlashCommands:Enabled"] = configuredValue })
                .Build();

            return new SlashCommandService(
                new Discord.WebSocket.DiscordSocketClient(),
                new CommandService(),
                Mock.Of<IVirtualCommandRunner>(),
                configuration,
                Mock.Of<ILogger<SlashCommandService>>());
        }

        // Stubs

        private sealed class StubCommand : ICommand
        {
            private readonly string _name;
            private readonly ApplicationCommandDeclaration _declaration;

            public StubCommand(string name, ApplicationCommandDeclaration declaration)
            {
                _name = name;
                _declaration = declaration;
            }

            public string Name => _name;
            public string Description { get; set; } = "A stub command for the definition contract";
            public string Usage => _name;
            public int DefaultPermissionTier => PermissionTier.Everyone;
            public ApplicationCommandDeclaration? ApplicationCommand => _declaration;

            public Task ExecuteCommand(IMessage message, ICommandContext context) => Task.CompletedTask;
        }
    }
}
