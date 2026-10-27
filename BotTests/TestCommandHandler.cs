using Discord;
using HambotV2.Commands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestCommandHandler
    {
        private class ThrowingCommand : ICommand
        {
            public string Name => "boom";
            public string Description => "Throws for testing the failure path";
            public string Usage => "boom";
            public int DefaultPermissionTier => 0;
            public CommandCategory Category => CommandCategory.General;

            public Task ExecuteCommand(IMessage socketMessage, ICommandContext context)
                => throw new InvalidOperationException("kaboom");
        }

        /// <summary>DbContext double that records disposal; in-memory backed, because the unknown-command path reads the custom responses.</summary>
        private sealed class RecordingDatabaseContext : DatabaseContext
        {
            public bool WasDisposed { get; private set; }

            public RecordingDatabaseContext() : base(new DbContextOptionsBuilder<DatabaseContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options) { }

            public override void Dispose()
            {
                WasDisposed = true;
                base.Dispose();
            }
        }

        /// <summary>Wires a CommandHandler with fully mocked Discord dependencies and records all channel replies.</summary>
        private sealed class TestPipeline
        {
            public const ulong GuildId = 1;
            public const ulong GuildOwnerId = 999;

            public Mock<ILogger<CommandHandler>> Logger { get; } = new();
            public Mock<IPermissionsService> Permissions { get; } = new();
            public Mock<IGuildSettingsService> GuildSettings { get; } = new();
            public Mock<ICooldownService> Cooldown { get; } = new();
            public Mock<IUser> Author { get; } = new();
            public Mock<IMessage> Message { get; } = new();
            public List<string> SentMessages { get; } = new();
            public RecordingDatabaseContext Database { get; } = new();

            public CommandService CommandService { get; }

            public TestPipeline(string content, bool directMessage = false)
            {
                CommandService = new CommandService();

                var channel = new Mock<IMessageChannel>();
                if (!directMessage)
                {
                    var guildChannel = channel.As<ITextChannel>();
                    var guild = new Mock<IGuild>();
                    guild.SetupGet(g => g.OwnerId).Returns(GuildOwnerId);
                    guildChannel.SetupGet(c => c.GuildId).Returns(GuildId);
                    guildChannel.SetupGet(c => c.Guild).Returns(guild.Object);
                }

                RecordSends(channel);
                GuildSettings.Setup(service => service.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");
                GuildSettings.SetupGet(service => service.GlobalCommandIdentifier).Returns("!");
                Cooldown
                    .Setup(cooldown => cooldown.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                    .Returns(CooldownCheck.Allowed);

                Author.SetupGet(a => a.Id).Returns(123456789UL);

                Message.SetupGet(m => m.Content).Returns(content);
                Message.SetupGet(m => m.Channel).Returns(channel.Object);
                Message.SetupGet(m => m.Author).Returns(Author.Object);
            }

            public CommandHandler CreateHandler()
            {
                return new CommandHandler(CommandService, GuildSettings.Object, Permissions.Object, Cooldown.Object, Database, Mock.Of<IConfiguration>(), Logger.Object);
            }

            public void AllowPermissions()
            {
                Permissions
                    .Setup(p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()))
                    .ReturnsAsync(new PermissionCheckResult(IsAllowed: true, UserEffectiveTier: PermissionTier.Everyone));
            }

            private void RecordSends(Mock<IMessageChannel> channel)
            {
                channel
                    .Setup(c => c.SendMessageAsync(
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<Embed?>(),
                        It.IsAny<RequestOptions?>(),
                        It.IsAny<AllowedMentions?>(),
                        It.IsAny<MessageReference?>(),
                        It.IsAny<MessageComponent>(),
                        It.IsAny<ISticker[]?>(),
                        It.IsAny<Embed[]?>(),
                        It.IsAny<MessageFlags>(),
                        It.IsAny<PollProperties?>()))
                    .Callback<string, bool, Embed?, RequestOptions?, AllowedMentions?, MessageReference?, MessageComponent, ISticker[]?, Embed[]?, MessageFlags, PollProperties?>(
                        (text, _, _, _, _, _, _, _, _, _, _) => SentMessages.Add(text))
                    .Returns(Task.FromResult<IUserMessage?>(null));
            }
        }

        private void VerifyErrorLoggedOnce(Mock<ILogger<CommandHandler>> logger)
        {
            logger.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((_, _) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        /// <summary>/// Verifies the structured command audit event: exactly one Information-level
        /// log whose rendered state matches the predicate — the single audit line per handled message.
        ///</summary>
        private static void VerifyAuditEvent(Mock<ILogger<CommandHandler>> logger, Func<string, bool> statePredicate, Func<Times> times)
        {
            logger.Verify(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state != null && statePredicate(state.ToString()!)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                times);
        }

        [TestMethod]
        public async Task HandleCommand_Allowed_LogsExecutedAuditEvent()
        {
            var pipeline = new TestPipeline("!greet");
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            pipeline.CommandService.RegisterCommand("greet", command.Object);
            pipeline.AllowPermissions();

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            VerifyAuditEvent(pipeline.Logger,
                state => state.Contains("Command greet handled with outcome Executed", StringComparison.Ordinal),
                Times.Once);
        }

        [TestMethod]
        public async Task HandleCommand_AuditEvent_DoesNotContainMessageContent()
        {
            var pipeline = new TestPipeline("!greet secret user text");
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            pipeline.CommandService.RegisterCommand("greet", command.Object);
            pipeline.AllowPermissions();

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            VerifyAuditEvent(pipeline.Logger,
                state => !state.Contains("secret", StringComparison.Ordinal),
                Times.Once);
        }

        [TestMethod]
        public async Task HandleCommand_PermissionDenied_LogsDeniedPermissionAuditEvent()
        {
            var pipeline = new TestPipeline("!greet");
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            pipeline.CommandService.RegisterCommand("greet", command.Object);
            pipeline.Permissions
                .Setup(p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), command.Object, It.IsAny<IDatabaseContext>()))
                .ReturnsAsync(new PermissionCheckResult(IsAllowed: false, UserEffectiveTier: PermissionTier.Everyone));

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            VerifyAuditEvent(pipeline.Logger,
                state => state.Contains("outcome DeniedPermission", StringComparison.Ordinal),
                Times.Once);
        }

        [TestMethod]
        public async Task HandleCommand_UnknownCommand_LogsUnknownCommandAuditEvent()
        {
            var pipeline = new TestPipeline("!gibberish");

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            VerifyAuditEvent(pipeline.Logger,
                state => state.Contains("outcome UnknownCommand", StringComparison.Ordinal),
                Times.Once);
        }

        [TestMethod]
        public async Task HandleCommand_BareIdentifier_LogsIgnoredAuditEvent()
        {
            var pipeline = new TestPipeline("!");

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            VerifyAuditEvent(pipeline.Logger,
                state => state.Contains("outcome Ignored", StringComparison.Ordinal),
                Times.Once);
        }

        [TestMethod]
        public async Task HandleCommand_CommandThrows_RepliesGenericError_LogsFailedAuditAndError()
        {
            var pipeline = new TestPipeline("!boom");
            pipeline.CommandService.RegisterCommand("boom", new ThrowingCommand());
            pipeline.AllowPermissions();

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            Assert.AreEqual(1, pipeline.SentMessages.Count);
            StringAssert.Contains(pipeline.SentMessages[0], "unexpected error");
            Assert.IsFalse(pipeline.SentMessages[0].Contains("kaboom"), "Exception details must not leak to Discord");
            VerifyAuditEvent(pipeline.Logger,
                state => state.Contains("outcome Failed", StringComparison.Ordinal),
                Times.Once);
            VerifyErrorLoggedOnce(pipeline.Logger);
        }

        [TestMethod]
        public async Task HandleCommand_DirectMessage_ExecutesCommand()
        {
            var pipeline = new TestPipeline("!greet", directMessage: true);
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            pipeline.CommandService.RegisterCommand("greet", command.Object);
            pipeline.AllowPermissions();

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            command.Verify(c => c.ExecuteCommand(pipeline.Message.Object, It.IsAny<ICommandContext>()), Times.Once);
            pipeline.Permissions.Verify(
                p => p.CheckUserPermissions(It.IsAny<ulong>(), null, It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()),
                Times.Once, "The DM permission check must run with a null guild id");
        }

        [TestMethod]
        public async Task HandleCommand_PermissionServiceThrows_RepliesWithGenericErrorWithoutExceptionDetails()
        {
            var pipeline = new TestPipeline("!greet");
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            pipeline.CommandService.RegisterCommand("greet", command.Object);
            pipeline.Permissions
                .Setup(p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), command.Object, It.IsAny<IDatabaseContext>()))
                .ThrowsAsync(new InvalidOperationException("database outage"));

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            Assert.AreEqual(1, pipeline.SentMessages.Count);
            StringAssert.Contains(pipeline.SentMessages[0], "unexpected error");
            Assert.IsFalse(pipeline.SentMessages[0].Contains("database outage"), "Exception details must not leak to Discord");
            command.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Never);
            VerifyErrorLoggedOnce(pipeline.Logger);
        }

        [TestMethod]
        public async Task HandleCommand_RepeatedFailures_KeepPipelineUsable()
        {
            var pipeline = new TestPipeline("!boom");
            pipeline.CommandService.RegisterCommand("boom", new ThrowingCommand());
            pipeline.AllowPermissions();

            var handler = pipeline.CreateHandler();
            await handler.HandleCommand(pipeline.Message.Object);
            await handler.HandleCommand(pipeline.Message.Object);

            Assert.AreEqual(2, pipeline.SentMessages.Count, "Each failed message must get its own error reply");
        }

        [TestMethod]
        public async Task HandleCommand_PermissionDenied_RepliesAndSkipsCommand()
        {
            var pipeline = new TestPipeline("!greet");
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            pipeline.CommandService.RegisterCommand("greet", command.Object);
            pipeline.Permissions
                .Setup(p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), command.Object, It.IsAny<IDatabaseContext>()))
                .ReturnsAsync(new PermissionCheckResult(IsAllowed: false, UserEffectiveTier: PermissionTier.Everyone));

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            Assert.AreEqual(1, pipeline.SentMessages.Count);
            StringAssert.Contains(pipeline.SentMessages[0], "not permitted");
            command.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Never);
        }

        [TestMethod]
        public async Task HandleCommand_UnknownCommand_RepliesUnavailableAndSkipsPermissionCheck()
        {
            var pipeline = new TestPipeline("!gibberish");
            pipeline.AllowPermissions();

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            Assert.AreEqual(1, pipeline.SentMessages.Count);
            StringAssert.Contains(pipeline.SentMessages[0], "unavailable");
            pipeline.Permissions.Verify(
                p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()),
                Times.Never);
        }

        [TestMethod]
        [DataRow("! quote")]
        [DataRow("! quote hello")]
        [DataRow("!   quote hello")]
        public async Task HandleCommand_SpacedCommandName_IsAnsweredUnavailableAndStoresNothing(string content)
        {
            var pipeline = new TestPipeline(content);
            pipeline.AllowPermissions();

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            Assert.AreEqual(1, pipeline.SentMessages.Count);
            Assert.AreEqual("The requested command is unavailable", pipeline.SentMessages[0]);
            Assert.AreEqual(0, await pipeline.Database.Quotes.CountAsync(), "A spaced spelling must never reach command execution");
            pipeline.Permissions.Verify(
                p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()),
                Times.Never);
        }

        [TestMethod]
        [DataRow("!")]
        [DataRow("!    ")]
        public async Task HandleCommand_BareIdentifier_IsIgnoredSilently(string content)
        {
            var pipeline = new TestPipeline(content);

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            Assert.AreEqual(0, pipeline.SentMessages.Count, "An identifier-only message must not be answered");
            pipeline.Permissions.Verify(
                p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()),
                Times.Never);
        }

        [TestMethod]
        public async Task HandleCommand_Allowed_ExecutesCommandWithoutReply()
        {
            var pipeline = new TestPipeline("!greet");
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            pipeline.CommandService.RegisterCommand("greet", command.Object);
            pipeline.AllowPermissions();

            await pipeline.CreateHandler().HandleCommand(pipeline.Message.Object);

            command.Verify(c => c.ExecuteCommand(pipeline.Message.Object, It.IsAny<ICommandContext>()), Times.Once);
            Assert.AreEqual(0, pipeline.SentMessages.Count);
        }

        [TestMethod]
        public void CommandHandler_Dispose_DisposesDatabaseContext()
        {
            var pipeline = new TestPipeline("!greet");

            pipeline.CreateHandler().Dispose();

            Assert.IsTrue(pipeline.Database.WasDisposed, "The per-message DatabaseContext must be disposed with its handler");
        }
    }
}
