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
    /// <summary>
    /// The shared fuzzy matcher at its boundaries and the unknown-command suggestion path
    /// through the real pipeline: typo → suggestion, garbage → plain reply, tier exclusion,
    /// the DM scope, and custom-response candidates.
    /// </summary>
    [TestClass]
    public sealed class TestFuzzyMatcher
    {
        private const ulong GuildId = 5;
        private const ulong GuildOwnerId = 999;
        private const ulong AuthorId = 200;

        [TestMethod]
        public void Distance_Of_Identical_Strings_Is_Zero()
        {
            Assert.AreEqual(0, FuzzyMatcher.Distance("quote", "quote"));
        }

        [TestMethod]
        public void Distance_Counts_Edits_Symmetrically()
        {
            Assert.AreEqual(3, FuzzyMatcher.Distance("kitten", "sitting"));
            Assert.AreEqual(3, FuzzyMatcher.Distance("sitting", "kitten"));
            Assert.AreEqual(1, FuzzyMatcher.Distance("quote", "quotes"));
        }

        [TestMethod]
        public void Distance_Of_An_Empty_String_Is_The_Other_Length()
        {
            Assert.AreEqual(4, FuzzyMatcher.Distance("", "help"));
            Assert.AreEqual(4, FuzzyMatcher.Distance("help", ""));
            Assert.AreEqual(0, FuzzyMatcher.Distance("", ""));
        }

        [TestMethod]
        public void Best_Picks_The_Lowest_Qualifying_Distance()
        {
            string? best = FuzzyMatcher.Best("ab", ["ax", "ad", "ac"], (_, distance) => distance <= 1);

            Assert.AreEqual("ac", best, "An equal distance is broken alphabetically.");
        }

        [TestMethod]
        public void Best_Ignores_Candidates_The_Qualifier_Rejects()
        {
            string? best = FuzzyMatcher.Best("ab", ["abcd", "abc"], (_, distance) => distance <= 1);

            Assert.AreEqual("abc", best, "A candidate beyond the qualification rule is never selected, even when nothing else qualifies better.");
        }

        [TestMethod]
        public void Best_Returns_Null_When_Nothing_Qualifies()
        {
            Assert.IsNull(FuzzyMatcher.Best("ab", [], (_, _) => true), "An empty candidate set yields no suggestion.");
            Assert.IsNull(FuzzyMatcher.Best("ab", ["abcd"], (_, distance) => distance <= 1), "A rejected candidate set yields no suggestion.");
        }

        /// <summary>Runs messages through the real CommandHandler over the real registry, with a controllable invoker tier.</summary>
        private sealed class SuggestionHarness
        {
            private readonly string _storeName = Guid.NewGuid().ToString();

            public CommandService CommandService { get; } = new();
            public Mock<IPermissionsService> Permissions { get; } = new();
            public Mock<IGuildSettingsService> GuildSettings { get; } = new();
            public Mock<ICooldownService> Cooldown { get; } = new();

            public SuggestionHarness(int invokerTier = 0)
            {
                GuildSettings.Setup(service => service.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");
                GuildSettings.SetupGet(service => service.GlobalCommandIdentifier).Returns("!");

                Permissions
                    .Setup(service => service.GetUserEffectiveTier(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<IDatabaseContext>()))
                    .ReturnsAsync(invokerTier);

                Cooldown
                    .Setup(service => service.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                    .Returns(CooldownCheck.Allowed);

                // One tier-0 custom response in the guild; a suggestion for it proves the
                // guild's response names are candidates alongside the built-ins.
                Store(response =>
                {
                    response.CustomResponses.Add(new DatabaseContext.CustomResponse
                    {
                        GuildId = GuildId,
                        Name = "pingpong",
                        Content = "pong",
                        LastModifiedByDiscordId = AuthorId,
                        CreatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                        UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime
                    });
                });
            }

            public async Task<List<string>> Handle(string content, ulong? guildId = GuildId)
            {
                var sentMessages = new List<string>();
                var message = guildId is ulong guild
                    ? DiscordTestDoubles.CreateGuildMessage(content, AuthorId, GuildOwnerId, sentMessages, guild)
                    : DiscordTestDoubles.CreateDirectMessage(content, AuthorId, sentMessages);

                using var database = CreateContext();
                using var handler = new CommandHandler(
                    CommandService, GuildSettings.Object, Permissions.Object, Cooldown.Object,
                    database, Mock.Of<IConfiguration>(), Mock.Of<ILogger<CommandHandler>>());
                await handler.HandleCommand(message.Object);

                return sentMessages;
            }

            public void Store(Action<DatabaseContext> seed)
            {
                using var database = CreateContext();
                seed(database);
                database.SaveChanges();
            }

            private DatabaseContext CreateContext()
                => new(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(_storeName).Options);
        }

        [TestMethod]
        public async Task UnknownCommand_WithACloseTypo_GetsOneSuggestion()
        {
            var harness = new SuggestionHarness();

            List<string> replies = await harness.Handle("!quoe");

            CollectionAssert.AreEqual(
                new List<string> { "The requested command is unavailable\nDid you mean !quote?" },
                replies);
        }

        [TestMethod]
        public async Task UnknownCommand_WithAPrefixToken_FindsTheClosestLongerCommand()
        {
            var harness = new SuggestionHarness();

            List<string> replies = await harness.Handle("!rem");

            CollectionAssert.AreEqual(
                new List<string> { "The requested command is unavailable\nDid you mean !remindme?" },
                replies,
                "A ≥3-character prefix qualifies; between remindme and reminders the smaller distance wins.");
        }

        [TestMethod]
        public async Task UnknownCommand_WithDistantGarbage_GetsThePlainReply()
        {
            var harness = new SuggestionHarness();

            List<string> replies = await harness.Handle("!zzqxxv");

            CollectionAssert.AreEqual(new List<string> { "The requested command is unavailable" }, replies);
        }

        [TestMethod]
        public async Task Suggestions_NeverLeakCommandsAboveTheInvokersTier()
        {
            var harness = new SuggestionHarness(invokerTier: 0);

            List<string> denied = await harness.Handle("!presenc");

            CollectionAssert.AreEqual(
                new List<string> { "The requested command is unavailable" },
                denied,
                "A close match above the invoker's tier is not suggested.");

            var elevated = new SuggestionHarness(invokerTier: 4);
            List<string> allowed = await elevated.Handle("!presenc");

            CollectionAssert.AreEqual(
                new List<string> { "The requested command is unavailable\nDid you mean !presence?" },
                allowed,
                "The same typo at bot-owner tier gets its suggestion.");
        }

        [TestMethod]
        public async Task Suggestions_InDirectMessages_ConsiderBuiltInsOnly()
        {
            var harness = new SuggestionHarness();

            List<string> typo = await harness.Handle("!quoe", guildId: null);
            CollectionAssert.AreEqual(
                new List<string> { "The requested command is unavailable\nDid you mean !quote?" },
                typo,
                "A built-in typo is suggested in DMs too.");

            List<string> responseTypo = await harness.Handle("!pingponx", guildId: null);
            CollectionAssert.AreEqual(
                new List<string> { "The requested command is unavailable" },
                responseTypo,
                "The guild's custom responses are not candidates in DMs.");
        }

        [TestMethod]
        public async Task Suggestions_IncludeTheGuildsCustomResponses()
        {
            var harness = new SuggestionHarness();

            List<string> replies = await harness.Handle("!pingponx");

            CollectionAssert.AreEqual(
                new List<string> { "The requested command is unavailable\nDid you mean !pingpong?" },
                replies);
        }

        [TestMethod]
        public async Task Suggestions_RespectACustomResponsesRaisedTier()
        {
            var harness = new SuggestionHarness(invokerTier: 0);
            harness.Store(database => database.Commands.Add(new DatabaseContext.CommandMetaData
            {
                GuildId = GuildId,
                CommandName = "pingpong",
                PermissionTier = PermissionTier.Moderator
            }));

            List<string> replies = await harness.Handle("!pingponx");

            CollectionAssert.AreEqual(
                new List<string> { "The requested command is unavailable" },
                replies,
                "A custom response raised above the invoker's tier is not suggested.");
        }
    }
}
