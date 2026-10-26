using Discord;
using HambotV2.Commands;
using HambotV2.Commands.ResponseCommands;
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
    public sealed class TestCustomResponses
    {
        private const ulong GuildId = 5;
        private const ulong OtherGuildId = 6;
        private const ulong AuthorId = 200;
        private const ulong GuildOwnerId = 999;

        /// <summary>
        /// Runs a response-management command against an isolated in-memory database and records
        /// the bot's replies. Guild messages belong to <see cref="GuildId"/> (or the given guild).
        /// </summary>
        private sealed class ResponseHarness
        {
            public DatabaseContext Database { get; } = DiscordTestDoubles.CreateDatabase();

            /// <summary>The real command registry, so built-in collision checks run against actual built-ins.</summary>
            public CommandService CommandService { get; } = new();

            public ResponseHarness()
            {
                CommandService.RegisterCommands();
            }

            public AddResponseCommand CreateAddResponseCommand() => new(CommandService);

            public async Task<List<string>> Execute(ICommand command, string content, ulong? guildId = GuildId, ulong authorId = AuthorId, DateTimeOffset? timestamp = null, IAttachment? attachment = null)
            {
                var sentMessages = new List<string>();
                var message = guildId is ulong guild
                    ? DiscordTestDoubles.CreateGuildMessage(content, authorId, GuildOwnerId, sentMessages, guild, timestamp, attachment)
                    : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);
                var context = DiscordTestDoubles.CreateContext(Database, guildId: guildId);

                await command.ExecuteCommand(message.Object, context);
                return sentMessages;
            }
        }

        /// <summary>/// A full CommandHandler pipeline over an isolated in-memory store: the real command
        /// registry (with one built-in test command), the real permission and cooldown services,
        /// and a fake clock, so dispatch order and gating are tested end to end.
        ///</summary>
        private sealed class PipelineHarness
        {
            private readonly string _storeName = Guid.NewGuid().ToString();

            public CommandService CommandService { get; } = new();
            public PermissionsService Permissions { get; } = new();
            public CooldownService Cooldown { get; }
            public FakeTimeProvider Time { get; } = new(DateTimeOffset.UnixEpoch);
            public Mock<ICommand> ExecutedBuiltIn { get; }

            public PipelineHarness()
            {
                var guildSettings = new Mock<IGuildSettingsService>();
                guildSettings.Setup(service => service.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");
                guildSettings.SetupGet(service => service.GlobalCommandIdentifier).Returns("!");
                GuildSettings = guildSettings;

                // The one registered built-in doubles as the dispatch-order probe: a custom
                ExecutedBuiltIn = DiscordTestDoubles.CreateMockCommand("greet");
                CommandService.RegisterCommand("greet", ExecutedBuiltIn.Object);

                Cooldown = new CooldownService(Time, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());
            }

            public Mock<IGuildSettingsService> GuildSettings { get; }

            public DatabaseContext CreateContext()
                => new(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(_storeName).Options);

            /// <summary>Handles a message through the real pipeline; fresh contexts over the shared store mimic the transient per-message contexts in production.</summary>
            public async Task<List<string>> Handle(string content, ulong? guildId = GuildId, ulong authorId = AuthorId)
            {
                var sentMessages = new List<string>();
                var message = guildId is ulong guild
                    ? DiscordTestDoubles.CreateGuildMessage(content, authorId, GuildOwnerId, sentMessages, guild)
                    : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);

                using var handler = new CommandHandler(
                    CommandService, GuildSettings.Object, Permissions, Cooldown,
                    CreateContext(), Mock.Of<IConfiguration>(), Mock.Of<ILogger<CommandHandler>>());
                await handler.HandleCommand(message.Object);

                return sentMessages;
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
        }

        private static ResponseHarness Harness() => new();

        [TestMethod]
        [DataRow("UPPER")]
        [DataRow("with.dot")]
        [DataRow("!bang")]
        [DataRow("café")]
        public async Task AddResponse_Rejects_InvalidNames(string name)
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();

            var replies = await harness.Execute(add, $"!addresponse {name} hello there");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "Invalid response name");
            Assert.AreEqual(0, harness.Database.CustomResponses.Count(), "Nothing must be stored for an invalid name");
        }

        [TestMethod]
        public async Task AddResponse_Rejects_NameLongerThan32Characters()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();
            string longName = new string('a', CustomResponseNames.MaxLength + 1);

            var replies = await harness.Execute(add, $"!addresponse {longName} hello");

            StringAssert.Contains(replies[0], "Invalid response name");
            Assert.AreEqual(0, harness.Database.CustomResponses.Count());
        }

        [TestMethod]
        [DataRow("plain")]
        [DataRow("with-digit2")]
        [DataRow("with_underscore")]
        public async Task AddResponse_Accepts_ValidNames(string name)
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();

            var replies = await harness.Execute(add, $"!addresponse {name} hello");

            CollectionAssert.AreEqual(new List<string> { $"Created the response '{name}'" }, replies);
            Assert.AreEqual(name, harness.Database.CustomResponses.Single().Name);
        }

        [TestMethod]
        public async Task AddResponse_WithoutArguments_UsageError()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();

            var replies = await harness.Execute(add, "!addresponse");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "Usage: !addresponse");
            Assert.AreEqual(0, harness.Database.CustomResponses.Count());
        }

        [TestMethod]
        public async Task AddResponse_Rejects_EmptyContent()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();

            var replies = await harness.Execute(add, "!addresponse name-only");

            CollectionAssert.AreEqual(new List<string> { "The response content must not be empty" }, replies);
            Assert.AreEqual(0, harness.Database.CustomResponses.Count());
        }

        [TestMethod]
        public async Task AddResponse_Accepts_Exactly1000Characters_AndRejects_1001()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();
            var timestamp = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

            CollectionAssert.AreEqual(new List<string> { "Created the response 'full'" },
                await harness.Execute(add, $"!addresponse full {new string('a', AddResponseCommand.MaxContentLength)}", timestamp: timestamp));
            Assert.AreEqual(AddResponseCommand.MaxContentLength, harness.Database.CustomResponses.Single().Content.Length);

            StringAssert.Contains(
                (await harness.Execute(add, $"!addresponse over {new string('b', AddResponseCommand.MaxContentLength + 1)}"))[0],
                "at most 1000 characters");
            Assert.AreEqual(1, harness.Database.CustomResponses.Count(), "The over-limit response must not be stored");
        }

        [TestMethod]
        public async Task AddResponse_Rejects_BuiltInCommandNames()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();

            // The harness registry carries all real built-ins; a couple of representatives plus
            // the response-management commands themselves must all be reserved.
            foreach (string builtIn in new[] { "quote", "help", "addresponse" })
            {
                var replies = await harness.Execute(add, $"!addresponse {builtIn} hello");
                StringAssert.Contains(replies[0], "built-in command");
            }

            Assert.AreEqual(0, harness.Database.CustomResponses.Count(), "No response may take a built-in's name");
        }

        [TestMethod]
        public async Task AddResponse_Creates_Then_Updates_WithDistinctRepliesAndFields()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();
            var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var updated = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

            CollectionAssert.AreEqual(new List<string> { "Created the response 'poke'" },
                await harness.Execute(add, "!addresponse poke first version", authorId: 100, timestamp: created));

            CollectionAssert.AreEqual(new List<string> { "Updated the response 'poke'" },
                await harness.Execute(add, "!addresponse poke second version", authorId: 200, timestamp: updated));

            var response = harness.Database.CustomResponses.Single();
            Assert.AreEqual("second version", response.Content);
            Assert.AreEqual(200UL, response.LastModifiedByDiscordId, "The last modifier's Discord id must be recorded");
            Assert.AreEqual(created.UtcDateTime, response.CreatedAt, "An update must keep the original creation time");
            Assert.AreEqual(updated.UtcDateTime, response.UpdatedAt, "The updated timestamp must reflect the update");
        }

        [TestMethod]
        public async Task RemoveResponse_Removes_Confirms_AndErrorsOnUnknownName()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();
            var remove = new RemoveResponseCommand();

            await harness.Execute(add, "!addresponse poke hello");

            CollectionAssert.AreEqual(new List<string> { "Removed the response 'poke'" },
                await harness.Execute(remove, "!removeresponse poke"));
            Assert.AreEqual(0, harness.Database.CustomResponses.Count());

            CollectionAssert.AreEqual(new List<string> { "There is no response named 'gone' in this server" },
                await harness.Execute(remove, "!removeresponse gone"));

            StringAssert.Contains((await harness.Execute(remove, "!removeresponse"))[0], "Usage: !removeresponse");
        }

        [TestMethod]
        public async Task Responses_Are_Isolated_PerGuild()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();
            var remove = new RemoveResponseCommand();
            var list = new ListResponsesCommand();

            await harness.Execute(add, "!addresponse poke guild five", guildId: GuildId);
            await harness.Execute(add, "!addresponse poke guild six", guildId: OtherGuildId);

            // The same name coexists in both guilds with independent content...
            Assert.AreEqual("guild five", harness.Database.CustomResponses.Single(r => r.GuildId == GuildId).Content);
            Assert.AreEqual("guild six", harness.Database.CustomResponses.Single(r => r.GuildId == OtherGuildId).Content);

            // ...and removal in one guild leaves the other untouched.
            await harness.Execute(remove, "!removeresponse poke", guildId: GuildId);
            CollectionAssert.AreEqual(new List<string> { "Custom responses: poke" },
                await harness.Execute(list, "!listresponses", guildId: OtherGuildId));
        }

        [TestMethod]
        public async Task ListResponses_ListsAlphabetically_AndReportsEmptyGuilds()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();
            var list = new ListResponsesCommand();

            CollectionAssert.AreEqual(
                new List<string> { "There are no custom responses in this server yet — add one with !addresponse <name> <content>" },
                await harness.Execute(list, "!listresponses"));

            foreach (string name in new[] { "zeta", "alpha", "mid-dot" })
                await harness.Execute(add, $"!addresponse {name} hello");

            CollectionAssert.AreEqual(
                new List<string> { "Custom responses: alpha, mid-dot, zeta" },
                await harness.Execute(list, "!listresponses"));
        }

        [TestMethod]
        public async Task AddResponse_Rejects_Attachments_AndStoresNothing()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();

            var replies = await harness.Execute(add, "!addresponse poke hello", attachment: DiscordTestDoubles.CreateAttachment());

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "text only");
            Assert.AreEqual(0, harness.Database.CustomResponses.Count(), "An invocation with an attachment must store nothing");
        }

        [TestMethod]
        public async Task ManagementCommands_DeclineInDirectMessages()
        {
            var harness = Harness();
            var add = harness.CreateAddResponseCommand();
            var remove = new RemoveResponseCommand();
            var list = new ListResponsesCommand();

            foreach (ICommand command in new ICommand[] { add, remove, list })
            {
                var replies = await harness.Execute(command, $"!{command.Name}", guildId: null);
                Assert.AreEqual(1, replies.Count, $"!{command.Name} must answer the DM decline exactly once");
                StringAssert.Contains(replies[0], "only available in servers");
            }

            Assert.AreEqual(0, harness.Database.CustomResponses.Count(), "A DM invocation must store nothing");
        }

        [TestMethod]
        public async Task Dispatch_BuiltInCommand_WinsOverCustomResponse()
        {
            var pipeline = new PipelineHarness();

            // A response stored under a built-in's name (e.g. created before a later release added
            // the built-in) must never shadow that built-in: the registry is consulted first.
            await pipeline.StoreResponse(GuildId, "greet", "shadow attempt");

            var replies = await pipeline.Handle("!greet");

            pipeline.ExecutedBuiltIn.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once,
                "The built-in must win dispatch");
            Assert.AreEqual(0, replies.Count, "The built-in mock sends no reply; the custom response content must not be sent");
        }

        [TestMethod]
        public async Task Dispatch_CustomResponse_WinsOverUnavailableReply()
        {
            var pipeline = new PipelineHarness();
            await pipeline.StoreResponse(GuildId, "poke", "go away");

            CollectionAssert.AreEqual(new List<string> { "go away" }, await pipeline.Handle("!poke"));

            // A guild with no response under the name still gets the plain unavailable reply.
            CollectionAssert.AreEqual(new List<string> { "The requested command is unavailable" }, await pipeline.Handle("!undefined"));
        }

        [TestMethod]
        public async Task Dispatch_CustomResponse_OnlyAppliesInItsOwnGuild()
        {
            var pipeline = new PipelineHarness();
            await pipeline.StoreResponse(GuildId, "poke", "go away");

            CollectionAssert.AreEqual(new List<string> { "The requested command is unavailable" }, await pipeline.Handle("!poke", guildId: OtherGuildId));
        }

        [TestMethod]
        public async Task Dispatch_InDirectMessages_KeepsUnavailableReply()
        {
            var pipeline = new PipelineHarness();
            await pipeline.StoreResponse(GuildId, "poke", "go away");

            CollectionAssert.AreEqual(new List<string> { "The requested command is unavailable" }, await pipeline.Handle("!poke", guildId: null));
        }

        [TestMethod]
        public async Task Dispatch_DefaultsToTier0_AndHonorsRuntimeCommandMetaDataOverride()
        {
            var pipeline = new PipelineHarness();
            await pipeline.StoreResponse(GuildId, "poke", "go away");

            // Default required tier 0: a user with no database entry may dispatch it.
            CollectionAssert.AreEqual(new List<string> { "go away" }, await pipeline.Handle("!poke"));

            // The required tier is adjustable at runtime like a built-in's (stored as regular
            // CommandMetaData under the response name).
            var database = pipeline.CreateContext();
            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = GuildId, CommandName = "poke", PermissionTier = PermissionTier.Admin });
            await database.SaveChangesAsync();

            CollectionAssert.AreEqual(new List<string> { "You are not permitted to use that command" }, await pipeline.Handle("!poke"));
        }

        [TestMethod]
        public async Task Dispatch_IsThrottledPerResponseName()
        {
            var pipeline = new PipelineHarness();
            await pipeline.StoreResponse(GuildId, "poke", "go away");

            CollectionAssert.AreEqual(new List<string> { "go away" }, await pipeline.Handle("!poke"));
            CollectionAssert.AreEqual(new List<string> { "go away" }, await pipeline.Handle("!poke", authorId: 300), "Another user has their own cooldown window");

            var throttled = await pipeline.Handle("!poke");
            Assert.AreEqual(1, throttled.Count);
            StringAssert.Contains(throttled[0], "too quickly", "A second invocation inside the window must be throttled");

            // The throttle keys on the response name, so an unrelated token is unaffected...
            CollectionAssert.AreEqual(new List<string> { "The requested command is unavailable" }, await pipeline.Handle("!undefined"));

            // ...and the response dispatches again once the window expires.
            pipeline.Time.Advance(TimeSpan.FromSeconds(3));
            CollectionAssert.AreEqual(new List<string> { "go away" }, await pipeline.Handle("!poke"));
        }

        [TestMethod]
        public async Task ManagementCommands_HaveConfiguredDefaultTiers()
        {
            var harness = Harness();
            var database = harness.Database;
            database.Users.Add(new DatabaseContext.User { GuildId = GuildId, DiscordId = 100, PermissionTier = PermissionTier.Everyone });
            database.Users.Add(new DatabaseContext.User { GuildId = GuildId, DiscordId = 101, PermissionTier = PermissionTier.Moderator });
            await database.SaveChangesAsync();

            var permissions = new PermissionsService();

            Assert.IsFalse((await permissions.CheckUserPermissions(100, GuildId, TierOverrides.None, harness.CreateAddResponseCommand(), database)).IsAllowed,
                "A tier-0 user must not manage responses");
            Assert.IsTrue((await permissions.CheckUserPermissions(101, GuildId, TierOverrides.None, harness.CreateAddResponseCommand(), database)).IsAllowed,
                "A moderator may add responses");
            Assert.IsFalse((await permissions.CheckUserPermissions(100, GuildId, TierOverrides.None, new RemoveResponseCommand(), database)).IsAllowed);
            Assert.IsTrue((await permissions.CheckUserPermissions(101, GuildId, TierOverrides.None, new RemoveResponseCommand(), database)).IsAllowed);

            Assert.IsTrue((await permissions.CheckUserPermissions(100, GuildId, TierOverrides.None, new ListResponsesCommand(), database)).IsAllowed);
        }

        [TestMethod]
        public void CustomResponseNames_Validates_TheSharedPattern()
        {
            foreach (string valid in new[] { "a", "plain", "with-digit2", "with-hyphen", "with_underscore", new string('x', CustomResponseNames.MaxLength) })
                Assert.IsTrue(CustomResponseNames.IsValid(valid), $"'{valid}' must be a valid response name");

            foreach (string invalid in new[] { "", "UPPER", "with space", "with.dot", "!", "ümlaut", new string('x', CustomResponseNames.MaxLength + 1) })
                Assert.IsFalse(CustomResponseNames.IsValid(invalid), $"'{invalid}' must be an invalid response name");
        }
    }
}
