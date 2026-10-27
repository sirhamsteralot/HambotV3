using Discord;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestBotBans
    {
        private const ulong GuildId = 7;
        private const ulong OtherGuildId = 8;
        private const ulong AuthorId = 200;
        private const ulong BannedUserId = 300;
        private const ulong TargetUserId = 400;
        private const ulong GuildOwnerId = 999;

        /// <summary>
        /// A full CommandHandler pipeline over an isolated in-memory store: the real command
        /// registry (with one built-in test command), the real permission and cooldown services,
        /// and a fake clock, so ban enforcement and lazy expiry are tested end to end.
        /// </summary>
        private sealed class PipelineHarness
        {
            public const ulong GuildId = 7;
            public const ulong GuildOwnerId = 999;

            private readonly string _storeName = Guid.NewGuid().ToString();
            private readonly Mock<IGuildSettingsService> _guildSettings;

            public CommandService CommandService { get; } = new();
            public PermissionsService Permissions { get; } = new();
            public FakeTimeProvider Time { get; } = new(DateTimeOffset.UnixEpoch);
            public Mock<ICommand> ExecutedBuiltIn { get; }
            public Mock<ILogger<CommandHandler>> Logger { get; } = new();
            public IConfiguration Configuration { get; set; } = Mock.Of<IConfiguration>();

            public PipelineHarness()
            {
                var guildSettings = new Mock<IGuildSettingsService>();
                guildSettings.Setup(service => service.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");
                guildSettings.SetupGet(service => service.GlobalCommandIdentifier).Returns("!");
                _guildSettings = guildSettings;

                ExecutedBuiltIn = DiscordTestDoubles.CreateMockCommand("greet");
                CommandService.RegisterCommand("greet", ExecutedBuiltIn.Object);
            }

            public DatabaseContext CreateContext()
                => new(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(_storeName).Options);

            public async Task StoreBan(ulong guildId, ulong discordId, DateTimeOffset? expiresAtUtc, ulong bannedBy = AuthorId)
            {
                var database = CreateContext();
                database.BotBans.Add(new DatabaseContext.BotBan
                {
                    GuildId = guildId,
                    DiscordId = discordId,
                    BannedByDiscordId = bannedBy,
                    CreatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                    ExpiresAtUtc = expiresAtUtc
                });
                await database.SaveChangesAsync();
            }

            public async Task StoreResponse(ulong guildId, string name, string content)
            {
                var database = CreateContext();
                database.CustomResponses.Add(new DatabaseContext.CustomResponse
                {
                    GuildId = guildId,
                    Name = name,
                    Content = content,
                    LastModifiedByDiscordId = AuthorId,
                    CreatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                    UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime
                });
                await database.SaveChangesAsync();
            }

            private Mock<ITextChannel> CreateGuildChannel(ulong guildId, List<string> sentMessages)
            {
                var guild = new Mock<IGuild>();
                guild.SetupGet(g => g.OwnerId).Returns(GuildOwnerId);
                var channel = new Mock<ITextChannel>();
                channel.SetupGet(c => c.GuildId).Returns(guildId);
                channel.SetupGet(c => c.Guild).Returns(guild.Object);
                RecordSends(channel, sentMessages);
                return channel;
            }

            /// <summary>Handles a message through the real pipeline; fresh contexts over the shared store mimic the transient per-message contexts in production.</summary>
            public async Task<List<string>> Handle(string content, ulong? guildId = GuildId, ulong authorId = AuthorId)
            {
                var sentMessages = new List<string>();
                var author = DiscordTestDoubles.CreateUser(authorId);
                IMessage message;

                if (guildId is ulong guild)
                {
                    var channel = CreateGuildChannel(guild, sentMessages);
                    message = new VirtualCommandMessage(author.Object, channel.Object, content, DateTimeOffset.UnixEpoch);
                }
                else
                {
                    var channel = new Mock<IMessageChannel>();
                    RecordSends(channel, sentMessages);
                    message = new VirtualCommandMessage(author.Object, channel.Object, content, DateTimeOffset.UnixEpoch);
                }

                using var handler = CreateHandler();
                await handler.HandleCommand(message);
                return sentMessages;
            }

            /// <summary>The same pipeline for an operator-agent invocation (the MCP run_command seam).</summary>
            public async Task<List<string>> HandleOperatorAgent(string content, ulong operatorAgentUserId)
            {
                var sentMessages = new List<string>();
                var channel = CreateGuildChannel(GuildId, sentMessages);
                var author = DiscordTestDoubles.CreateUser(operatorAgentUserId);
                var message = new VirtualCommandMessage(author.Object, channel.Object, content, DateTimeOffset.UnixEpoch, operatorAgentUserId);

                using var handler = CreateHandler();
                await handler.HandleCommand(message);
                return sentMessages;
            }

            private CommandHandler CreateHandler()
            {
                return new CommandHandler(
                    CommandService, _guildSettings.Object, Permissions,
                    new CooldownService(Time, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build()),
                    CreateContext(), Configuration, Logger.Object, timeProvider: Time);
            }

            private static void RecordSends<TChannel>(Mock<TChannel> channel, List<string> sentMessages) where TChannel : class, IMessageChannel
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
                        (text, _, _, _, _, _, _, _, _, _, _) => sentMessages.Add(text))
                    .Returns(Task.FromResult<IUserMessage?>(null));
            }
        }

        /// <summary>Runs a ban command against an isolated in-memory database and records the bot's replies.</summary>
        private sealed class BanHarness
        {
            public DatabaseContext Database { get; } = DiscordTestDoubles.CreateDatabase();
            public List<string> SentMessages { get; } = new();

            public async Task<List<string>> Execute(ICommand command, string content, ulong? guildId = GuildId, ulong authorId = AuthorId, ulong? guildOwnerId = null, ulong? botOwnerId = null)
            {
                var message = guildId is ulong guild
                    ? DiscordTestDoubles.CreateGuildMessage(content, authorId, guildOwnerId ?? GuildOwnerId, SentMessages, guild)
                    : DiscordTestDoubles.CreateDirectMessage(content, authorId, SentMessages);
                var context = DiscordTestDoubles.CreateContext(Database, guildOwnerId: guildOwnerId, botOwnerId: botOwnerId, guildId: guildId);

                await command.ExecuteCommand(message.Object, context);
                return SentMessages;
            }

            public async Task GiveTier(ulong discordId, int tier)
            {
                Database.Users.Add(new DatabaseContext.User { GuildId = GuildId, DiscordId = discordId, PermissionTier = tier });
                await Database.SaveChangesAsync();
            }

            public async Task StoreBan(ulong discordId, DateTimeOffset? expiresAtUtc = null, ulong bannedBy = GuildOwnerId)
            {
                Database.BotBans.Add(new DatabaseContext.BotBan
                {
                    GuildId = GuildId,
                    DiscordId = discordId,
                    BannedByDiscordId = bannedBy,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAtUtc = expiresAtUtc
                });
                await Database.SaveChangesAsync();
            }
        }

        private static BanHarness Harness() => new();

        [TestMethod]
        public async Task Pipeline_BannedUser_BuiltInCommand_Is_Ignored_Silently()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, DateTimeOffset.UnixEpoch.AddDays(1));

            var replies = await harness.Handle("!greet", authorId: BannedUserId);

            Assert.AreEqual(0, replies.Count, "A banned user must get no reply at all");
            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Never);
        }

        [TestMethod]
        public async Task Pipeline_PermanentBan_Also_Blocks()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, expiresAtUtc: null);

            var replies = await harness.Handle("!greet", authorId: BannedUserId);

            Assert.AreEqual(0, replies.Count);
            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Never);
        }

        [TestMethod]
        public async Task Pipeline_BannedUser_UnknownCommand_Is_Ignored_Silently()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, expiresAtUtc: null);

            var replies = await harness.Handle("!gibberish", authorId: BannedUserId);

            Assert.AreEqual(0, replies.Count, "Even the 'unavailable' reply is withheld from a banned user");
        }

        [TestMethod]
        public async Task Pipeline_BannedUser_CustomResponse_Is_Ignored_Silently()
        {
            var harness = new PipelineHarness();
            await harness.StoreResponse(PipelineHarness.GuildId, "ping", "pong");
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, expiresAtUtc: null);

            var replies = await harness.Handle("!ping", authorId: BannedUserId);

            Assert.AreEqual(0, replies.Count);
        }

        [TestMethod]
        public async Task Pipeline_UnbannedUser_Is_Unaffected()
        {
            var harness = new PipelineHarness();

            await harness.Handle("!greet");

            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once);
        }

        [TestMethod]
        public async Task Pipeline_Ban_Logs_Banned_Audit_Event()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, expiresAtUtc: null);

            await harness.Handle("!greet", authorId: BannedUserId);

            harness.Logger.Verify(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state != null && state.ToString()!.Contains("outcome Banned", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [TestMethod]
        public async Task Pipeline_Ban_Is_PerGuild_Other_Guild_Is_Unaffected()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, expiresAtUtc: null);

            await harness.Handle("!greet", guildId: OtherGuildId, authorId: BannedUserId);

            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once);
        }

        [TestMethod]
        public async Task Pipeline_Ban_Does_Not_Reach_Direct_Messages()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, expiresAtUtc: null);

            await harness.Handle("!greet", guildId: null, authorId: BannedUserId);

            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once);
        }

        [TestMethod]
        public async Task Pipeline_ExpiredTemporaryBan_Lifts_Itself()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, DateTimeOffset.UnixEpoch.AddSeconds(-1));

            await harness.Handle("!greet", authorId: BannedUserId);

            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once, "An expired ban must not block the invocation");

            var database = harness.CreateContext();
            Assert.AreEqual(0, database.BotBans.Count(), "The expired record must be deleted on sight");
        }

        [TestMethod]
        public async Task Pipeline_GuildOwner_Is_Exempt()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, PipelineHarness.GuildOwnerId, expiresAtUtc: null);

            await harness.Handle("!greet", authorId: PipelineHarness.GuildOwnerId);

            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once);
        }

        [TestMethod]
        public async Task Pipeline_BotOwner_Is_Exempt()
        {
            var harness = new PipelineHarness
            {
                Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Permissions:BotOwnerDiscordId"] = BannedUserId.ToString()
                }).Build()
            };
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, expiresAtUtc: null);

            await harness.Handle("!greet", authorId: BannedUserId);

            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once);
        }

        [TestMethod]
        public async Task Pipeline_OperatorAgent_Is_Exempt()
        {
            var harness = new PipelineHarness();
            await harness.StoreBan(PipelineHarness.GuildId, BannedUserId, expiresAtUtc: null);

            await harness.HandleOperatorAgent("!greet", BannedUserId);

            harness.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once, "The MCP run_command tool is the operator's agent, not the banned user");
        }

        [TestMethod]
        public void BanCommands_Resolve_From_The_Registry()
        {
            ICommandService commandService = new CommandService();
            commandService.RegisterCommands();

            Assert.IsTrue(commandService.TryGetCommand("!banuser", "!", out ICommand? banUser));
            Assert.IsInstanceOfType(banUser, typeof(BanUserCommand));
            Assert.AreEqual(PermissionTier.Moderator, banUser!.DefaultPermissionTier);

            Assert.IsTrue(commandService.TryGetCommand("!unbanuser", "!", out ICommand? unbanUser));
            Assert.IsInstanceOfType(unbanUser, typeof(UnbanUserCommand));
            Assert.AreEqual(PermissionTier.Moderator, unbanUser!.DefaultPermissionTier);

            Assert.IsTrue(commandService.TryGetCommand("!banlist", "!", out ICommand? banList));
            Assert.IsInstanceOfType(banList, typeof(BanListCommand));
            Assert.AreEqual(PermissionTier.Moderator, banList!.DefaultPermissionTier);
        }

        [TestMethod]
        public async Task BanUser_Creates_Permanent_Ban()
        {
            var harness = Harness();
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, $"!banuser {TargetUserId}", guildOwnerId: AuthorId);

            StringAssert.Contains(replies[0], $"Banned user {TargetUserId} from using the bot: permanent");

            var ban = harness.Database.BotBans.Single(stored => stored.GuildId == GuildId && stored.DiscordId == TargetUserId);
            Assert.IsNull(ban.ExpiresAtUtc);
            Assert.AreEqual(AuthorId, ban.BannedByDiscordId);
        }

        [TestMethod]
        public async Task BanUser_Creates_Temporary_Ban_From_Duration_Tokens()
        {
            var harness = Harness();
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, $"!banuser {TargetUserId} 1d 2h", guildOwnerId: AuthorId);

            StringAssert.Contains(replies[0], "until");
            var ban = harness.Database.BotBans.Single(stored => stored.GuildId == GuildId && stored.DiscordId == TargetUserId);
            Assert.IsTrue(ban.ExpiresAtUtc > DateTimeOffset.UtcNow.AddHours(25), "The ban must expire about 1d 2h from now");
        }

        [TestMethod]
        public async Task BanUser_Updates_Existing_Ban()
        {
            var harness = Harness();
            await harness.StoreBan(TargetUserId, DateTimeOffset.UtcNow.AddDays(1));
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, $"!banuser {TargetUserId}", guildOwnerId: AuthorId);

            StringAssert.Contains(replies[0], $"Updated the bot ban for user {TargetUserId}");
            Assert.AreEqual(1, harness.Database.BotBans.Count());
            var ban = harness.Database.BotBans.Single();
            Assert.IsNull(ban.ExpiresAtUtc, "Re-banning without a duration makes the ban permanent");
        }

        [TestMethod]
        [DataRow("!banuser")]
        [DataRow("!banuser notanumber")]
        public async Task BanUser_Rejects_Missing_Or_Unparseable_User(string content)
        {
            var harness = Harness();
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, content, guildOwnerId: AuthorId);

            Assert.AreEqual(1, replies.Count);
            Assert.AreEqual(0, harness.Database.BotBans.Count());
        }

        [TestMethod]
        [DataRow("!banuser 400 flooding")]
        [DataRow("!banuser 400 1d flooding")]
        [DataRow("!banuser 400 1d 2h extra")]
        public async Task BanUser_Rejects_Anything_Beyond_User_And_Duration(string content)
        {
            var harness = Harness();
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, content, guildOwnerId: AuthorId);

            StringAssert.Contains(replies[0], "takes a user and an optional duration");
            Assert.AreEqual(0, harness.Database.BotBans.Count());
        }

        [TestMethod]
        public async Task BanUser_Rejects_Self_Ban()
        {
            var harness = Harness();
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, $"!banuser {AuthorId}", guildOwnerId: AuthorId);

            StringAssert.Contains(replies[0], "cannot ban yourself");
            Assert.AreEqual(0, harness.Database.BotBans.Count());
        }

        [TestMethod]
        public async Task BanUser_Rejects_Guild_Owner_As_Target()
        {
            var harness = Harness();
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, $"!banuser {GuildOwnerId}", authorId: TargetUserId, guildOwnerId: GuildOwnerId, botOwnerId: AuthorId);

            StringAssert.Contains(replies[0], "cannot be banned");
            Assert.AreEqual(0, harness.Database.BotBans.Count());
        }

        [TestMethod]
        public async Task BanUser_Escalation_Guard_Rejects_Target_At_Invoker_Tier()
        {
            var harness = Harness();
            await harness.GiveTier(AuthorId, PermissionTier.Moderator);
            await harness.GiveTier(TargetUserId, PermissionTier.Moderator);
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, $"!banuser {TargetUserId}");

            StringAssert.Contains(replies[0], "below your own tier");
            Assert.AreEqual(0, harness.Database.BotBans.Count());
        }

        [TestMethod]
        public async Task BanUser_Declines_In_Direct_Messages()
        {
            var harness = Harness();
            var command = new BanUserCommand();

            var replies = await harness.Execute(command, $"!banuser {TargetUserId}", guildId: null);

            StringAssert.Contains(replies[0], "only available in servers");
            Assert.AreEqual(0, harness.Database.BotBans.Count());
        }

        [TestMethod]
        public async Task UnbanUser_Removes_The_Ban()
        {
            var harness = Harness();
            await harness.StoreBan(TargetUserId);
            var command = new UnbanUserCommand();

            var replies = await harness.Execute(command, $"!unbanuser {TargetUserId}");

            StringAssert.Contains(replies[0], $"Unbanned user {TargetUserId}");
            Assert.AreEqual(0, harness.Database.BotBans.Count());
        }

        [TestMethod]
        public async Task UnbanUser_Unknown_User_Replies_Not_Banned()
        {
            var harness = Harness();
            var command = new UnbanUserCommand();

            var replies = await harness.Execute(command, $"!unbanuser {TargetUserId}");

            StringAssert.Contains(replies[0], "not banned");
        }

        [TestMethod]
        public async Task UnbanUser_Declines_In_Direct_Messages()
        {
            var harness = Harness();
            var command = new UnbanUserCommand();

            var replies = await harness.Execute(command, $"!unbanuser {TargetUserId}", guildId: null);

            StringAssert.Contains(replies[0], "only available in servers");
        }

        [TestMethod]
        public async Task BanList_Empty_Replies_Friendly_Message()
        {
            var harness = Harness();
            var command = new BanListCommand();

            var replies = await harness.Execute(command, "!banlist");

            StringAssert.Contains(replies[0], "No users are banned");
        }

        [TestMethod]
        public async Task BanList_Lists_Permanent_And_Temporary_Bans()
        {
            var harness = Harness();
            await harness.StoreBan(TargetUserId);
            await harness.StoreBan(BannedUserId, DateTimeOffset.UtcNow.AddHours(2), bannedBy: AuthorId);
            var command = new BanListCommand();

            var replies = await harness.Execute(command, "!banlist");

            string listing = replies[0];
            StringAssert.Contains(listing, $"{TargetUserId} — permanent");
            StringAssert.Contains(listing, $"{BannedUserId} — until");
            StringAssert.Contains(listing, $"banned by {AuthorId}");
        }

        [TestMethod]
        public async Task BanList_Lifts_Expired_Bans()
        {
            var harness = Harness();
            await harness.StoreBan(TargetUserId, DateTimeOffset.UtcNow.AddHours(-1));
            var command = new BanListCommand();

            var replies = await harness.Execute(command, "!banlist");

            StringAssert.Contains(replies[0], "No users are banned");
            Assert.AreEqual(0, harness.Database.BotBans.Count(), "The expired record must be deleted on sight");
        }

        [TestMethod]
        public async Task BanList_Caps_At_Ten_Entries()
        {
            var harness = Harness();
            for (ulong userId = 1; userId <= 12; userId++)
                await harness.StoreBan(userId);
            var command = new BanListCommand();

            var replies = await harness.Execute(command, "!banlist");

            StringAssert.Contains(replies[0], "… and 2 more");
        }

        [TestMethod]
        public async Task BanList_Declines_In_Direct_Messages()
        {
            var harness = Harness();
            var command = new BanListCommand();

            var replies = await harness.Execute(command, "!banlist", guildId: null);

            StringAssert.Contains(replies[0], "only available in servers");
        }
    }
}
