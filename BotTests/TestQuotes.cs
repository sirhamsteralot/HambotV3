using Discord;
using HambotV2.Commands;
using HambotV2.Commands.QuoteCommands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestQuotes
    {
        private const ulong GuildId = 5;
        private const ulong OtherGuildId = 6;
        private const ulong AuthorId = 200;

        /// <summary>
        /// Runs a quote command against an isolated in-memory database and records the bot's
        /// replies. Guild messages belong to <see cref="GuildId"/> (or the given guild).
        /// </summary>
        private sealed class QuoteHarness
        {
            public DatabaseContext Database { get; } = DiscordTestDoubles.CreateDatabase();

            public async Task<List<string>> Execute(ICommand command, string content, ulong? guildId = GuildId, ulong authorId = AuthorId, DateTimeOffset? timestamp = null, IAttachment? attachment = null, List<MessageComponent?>? sentComponents = null)
            {
                var sentMessages = new List<string>();
                var message = guildId is ulong guild
                    ? DiscordTestDoubles.CreateGuildMessage(content, authorId, 999, sentMessages, guild, timestamp, attachment, sentComponents)
                    : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);
                var context = DiscordTestDoubles.CreateContext(Database, guildId: guildId);

                await command.ExecuteCommand(message.Object, context);
                return sentMessages;
            }
        }

        /// <summary>Downloader double for !importquotes: returns canned content or throws.</summary>
        private sealed class FakeAttachmentDownloader : IAttachmentContentDownloader
        {
            public string? Content { get; set; }
            public Exception? Failure { get; set; }
            public List<string> RequestedUrls { get; } = new();

            public Task<string> DownloadTextAsync(string url)
            {
                RequestedUrls.Add(url);
                return Failure is not null ? Task.FromException<string>(Failure) : Task.FromResult(Content!);
            }
        }

        private static QuoteHarness Harness() => new();

        /// <summary>A search command over a fresh backlog; the click tests build their own to share with the search.</summary>
        private static SearchQuotesCommand Search() => new(new QuoteSearchBacklog());

        [TestMethod]
        public async Task Add_Assigns_Sequential_Numbers_And_Records_Adder_And_Timestamp()
        {
            var harness = Harness();
            var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

            CollectionAssert.AreEqual(new List<string> { "Added quote #0" }, await harness.Execute(new QuoteCommand(), "!quote first", timestamp: timestamp));
            CollectionAssert.AreEqual(new List<string> { "Added quote #1" }, await harness.Execute(new QuoteCommand(), "!quote second"));
            CollectionAssert.AreEqual(new List<string> { "Added quote #2" }, await harness.Execute(new QuoteCommand(), "!quote third"));

            var quotes = harness.Database.Quotes.OrderBy(quote => quote.Number).ToList();
            Assert.AreEqual(3, quotes.Count);
            Assert.AreEqual(("first", 0), (quotes[0].Text, quotes[0].Number));
            Assert.AreEqual(("second", 1), (quotes[1].Text, quotes[1].Number));
            Assert.AreEqual(("third", 2), (quotes[2].Text, quotes[2].Number));
            Assert.AreEqual(AuthorId, quotes[0].AddedByDiscordId, "The adder's Discord id must be recorded");
            Assert.AreEqual(timestamp.UtcDateTime, quotes[0].CreatedAt, "The adding message's timestamp must be recorded");
        }

        [TestMethod]
        public async Task Remove_Never_Renumbers_And_Top_Number_Is_Reused()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            var remove = new RemoveQuoteCommand();

            foreach (string text in new[] { "one", "two", "three" })
                await harness.Execute(quote, $"!quote {text}");

            CollectionAssert.AreEqual(new List<string> { "Removed quote #1" }, await harness.Execute(remove, "!removequote 1"));
            CollectionAssert.AreEqual(new[] { 0, 2 }, harness.Database.Quotes.OrderBy(item => item.Number).Select(item => item.Number).ToList());

            // ...and removing the highest-numbered quote lets the next added quote reuse that
            // top number (the assignment always follows the current maximum).
            await harness.Execute(quote, "!quote four");
            CollectionAssert.AreEqual(new List<string> { "Removed quote #3" }, await harness.Execute(remove, "!removequote 3"));
            CollectionAssert.AreEqual(new List<string> { "Added quote #3" }, await harness.Execute(quote, "!quote fresh"));

            var last = harness.Database.Quotes.OrderBy(item => item.Number).Last();
            Assert.AreEqual("fresh", last.Text);
            Assert.AreEqual(3, last.Number);
        }

        [TestMethod]
        public async Task Quotes_Are_Isolated_Per_Guild()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            var remove = new RemoveQuoteCommand();
            var search = Search();

            await harness.Execute(quote, "!quote alpha", guildId: GuildId);
            await harness.Execute(quote, "!quote beta", guildId: OtherGuildId);

            // Numbers are per-guild: the second guild also starts at 0.
            CollectionAssert.AreEqual(new List<string> { "#0: alpha" }, await harness.Execute(quote, "!quote 0", guildId: GuildId));
            CollectionAssert.AreEqual(new List<string> { "#0: beta" }, await harness.Execute(quote, "!quote 0", guildId: OtherGuildId));

            // A quote removed in one guild stays untouched in the other...
            await harness.Execute(remove, "!removequote 0", guildId: GuildId);
            Assert.AreEqual(1, harness.Database.Quotes.Count(item => item.GuildId == OtherGuildId));

            // ...and quotes are never visible, searchable, or removable across guilds.
            CollectionAssert.AreEqual(
                new List<string> { "No quotes match 'alpha' in this server" },
                await harness.Execute(search, "!searchquotes alpha", guildId: OtherGuildId));
            CollectionAssert.AreEqual(
                new List<string> { "There is no quote with number 0 in this server" },
                await harness.Execute(quote, "!quote 0", guildId: GuildId));
        }

        [TestMethod]
        public async Task Random_Fetch_Empty_Guild_Gets_Friendly_Message()
        {
            var harness = Harness();

            var replies = await harness.Execute(new QuoteCommand(), "!quote");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "no quotes");
        }

        [TestMethod]
        public async Task Random_Fetch_Populated_Guild_Returns_Number_Prefixed_Quote()
        {
            var harness = Harness();
            foreach (string text in new[] { "one", "two", "three" })
                await harness.Execute(new QuoteCommand(), $"!quote {text}");

            var replies = await harness.Execute(new QuoteCommand(), "!quote");

            Assert.AreEqual(1, replies.Count);
            Assert.IsTrue(
                replies[0] == "#0: one" || replies[0] == "#1: two" || replies[0] == "#2: three",
                $"Expected a number-prefixed quote of the guild, got '{replies[0]}'");
        }

        [TestMethod]
        public async Task Fetch_By_Number_Returns_That_Quote()
        {
            var harness = Harness();
            foreach (string text in new[] { "one", "two" })
                await harness.Execute(new QuoteCommand(), $"!quote {text}");

            CollectionAssert.AreEqual(new List<string> { "#1: two" }, await harness.Execute(new QuoteCommand(), "!quote 1"));
        }

        [TestMethod]
        public async Task Fetch_By_Unknown_Number_Produces_In_Channel_Error_Not_Exception()
        {
            var harness = Harness();

            var replies = await harness.Execute(new QuoteCommand(), "!quote 42");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "no quote with number 42");
        }

        [TestMethod]
        public async Task Number_Above_Int_Range_Is_An_Unknown_Number_Not_Text()
        {
            var harness = Harness();

            // Within long range, so it selects a quote number — none can exist that high.
            var replies = await harness.Execute(new QuoteCommand(), "!quote 3000000000");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "no quote with number 3000000000");
            Assert.AreEqual(0, harness.Database.Quotes.Count(), "Nothing must be stored");
        }

        [TestMethod]
        public async Task Add_By_Text_Stores_The_Remaining_Text_And_Replies_With_The_Number()
        {
            var harness = Harness();

            CollectionAssert.AreEqual(new List<string> { "Added quote #0" }, await harness.Execute(new QuoteCommand(), "!quote some great moment"));

            var quote = harness.Database.Quotes.Single();
            Assert.AreEqual("some great moment", quote.Text);
        }

        [TestMethod]
        public async Task Numeric_Looking_Argument_Beyond_Long_Range_Is_Added_As_Quote_Text()
        {
            var harness = Harness();

            CollectionAssert.AreEqual(new List<string> { "Added quote #0" }, await harness.Execute(new QuoteCommand(), "!quote 99999999999999999999999999999"));

            Assert.AreEqual("99999999999999999999999999999", harness.Database.Quotes.Single().Text);
        }

        [TestMethod]
        public async Task Add_Accepts_Up_To_2000_Characters_And_Rejects_More()
        {
            var harness = Harness();
            string boundary = new string('a', 2000);
            string overLimit = new string('b', 2001);

            CollectionAssert.AreEqual(new List<string> { "Added quote #0" }, await harness.Execute(new QuoteCommand(), $"!quote {boundary}"));
            Assert.AreEqual(boundary, harness.Database.Quotes.Single().Text);

            // ...and anything longer is rejected in-channel with nothing stored.
            var replies = await harness.Execute(new QuoteCommand(), $"!quote {overLimit}");
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "at most 2000 characters");
            Assert.AreEqual(1, harness.Database.Quotes.Count(), "The over-long text must not be stored");
        }

        [TestMethod]
        public async Task Fetch_Of_Maximal_Quote_Trims_The_Reply_To_The_Message_Limit()
        {
            var harness = Harness();
            harness.Database.Quotes.Add(new DatabaseContext.Quote { GuildId = GuildId, Number = 0, Text = new string('c', 2000) });
            harness.Database.SaveChanges();

            // The stored text plus its number prefix exceeds Discord's message limit, so the
            // reply is trimmed to fit — the fetch never fails and the stored text stays intact.
            var replies = await harness.Execute(new QuoteCommand(), "!quote 0");

            Assert.AreEqual(1, replies.Count);
            Assert.IsTrue(replies[0].Length <= 2000, $"The reply must fit a Discord message, got {replies[0].Length} chars");
            Assert.IsTrue(replies[0].StartsWith("#0: "), "The number prefix must be kept");
            Assert.IsTrue(replies[0].EndsWith("…"), "A trimmed reply must end with an ellipsis");
        }

        [TestMethod]
        public async Task Search_Is_Case_Insensitive_Number_Prefixed_And_Ordered()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            await harness.Execute(quote, "!quote Big Dog");
            await harness.Execute(quote, "!quote small cat");
            await harness.Execute(quote, "!quote another DOG here");

            var replies = await harness.Execute(Search(), "!searchquotes dog");

            Assert.AreEqual(1, replies.Count);
            Assert.AreEqual("Quotes matching 'dog':" + Environment.NewLine + "#0: Big Dog" + Environment.NewLine + "#2: another DOG here", replies[0]);
        }

        [TestMethod]
        public async Task Search_Multi_Word_Term_Matches_The_Joined_Text()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote a big dog barks");

            CollectionAssert.AreEqual(new List<string> { "Quotes matching 'big dog':" + Environment.NewLine + "#0: a big dog barks" }, await harness.Execute(Search(), "!searchquotes big dog"));
        }

        [TestMethod]
        public async Task Search_Pages_All_Matches_And_Attaches_Buttons()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            for (int number = 1; number <= 12; number++)
                await harness.Execute(quote, $"!quote common text {number}");

            var sentComponents = new List<MessageComponent?>();
            var replies = await harness.Execute(Search(), "!searchquotes common", sentComponents: sentComponents);

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "(page 1 of 2):");
            Assert.AreEqual(11, replies[0].Split(Environment.NewLine).Length, "One page holds the header and 10 results");
            StringAssert.Contains(replies[0], "#9: common text 10", "The first page keeps the relevance order");
            Assert.IsFalse(replies[0].Contains("#10:"), "Only the page's ten results are sent");
            Assert.IsNotNull(sentComponents[0], "A multi-page search attaches the previous/next buttons");
        }

        [TestMethod]
        public async Task Search_That_Fits_One_Page_Has_No_Buttons()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote a big dog barks");

            var sentComponents = new List<MessageComponent?>();
            var replies = await harness.Execute(Search(), "!searchquotes big dog", sentComponents: sentComponents);

            Assert.AreEqual("Quotes matching 'big dog':" + Environment.NewLine + "#0: a big dog barks", replies[0]);
            Assert.IsNull(sentComponents[0], "A search that fits one page has no buttons to attach");
        }

        [TestMethod]
        public async Task Search_Keeps_At_Most_The_Hundred_Most_Relevant_Matches()
        {
            var harness = Harness();
            for (int number = 0; number < 105; number++)
                harness.Database.Quotes.Add(new DatabaseContext.Quote { GuildId = GuildId, Number = number, Text = $"common text {number}" });
            harness.Database.SaveChanges();

            var replies = await harness.Execute(Search(), "!searchquotes common");

            StringAssert.Contains(replies[0], "(page 1 of 10, showing the 100 most relevant):");
            Assert.AreEqual(11, replies[0].Split(Environment.NewLine).Length, "One page holds the header and 10 results");
            Assert.IsFalse(replies[0].Contains("#10:"), "Only the page's ten results are sent");
        }

        /// <summary>Moq double of one pagination button click, wired for the click-handling tests.</summary>
        private static IPaginationButton CreatePaginationClick(ulong clickerId, string customId, out Mock<IPaginationButton> button)
        {
            var user = new Mock<IUser>();
            user.SetupGet(u => u.Id).Returns(clickerId);

            button = new Mock<IPaginationButton>();
            button.SetupGet(b => b.User).Returns(user.Object);
            button.SetupGet(b => b.CustomId).Returns(customId);
            button.SetupGet(b => b.Channel).Returns(new Mock<IMessageChannel>().Object);
            button.SetupGet(b => b.CreatedAt).Returns(DateTimeOffset.UnixEpoch);
            button.Setup(b => b.DeferAsync()).Returns(Task.CompletedTask);
            button.Setup(b => b.SendEphemeralAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
            button.Setup(b => b.ModifyListInPlaceAsync(It.IsAny<string>(), It.IsAny<MessageComponent>())).Returns(Task.CompletedTask);
            return button.Object;
        }

        [TestMethod]
        public async Task Search_Button_Click_Pages_The_Invoker_Through_The_Ranked_Backlog()
        {
            var harness = Harness();
            var backlog = new QuoteSearchBacklog();
            var quote = new QuoteCommand();
            for (int number = 1; number <= 12; number++)
                await harness.Execute(quote, $"!quote common text {number}");

            await harness.Execute(new SearchQuotesCommand(backlog), "!searchquotes common");

            // A fresh backlog's first stored search has id 0; the next button targets its second page.
            var click = CreatePaginationClick(AuthorId, QuoteSearchPagination.CustomId(AuthorId, 0, 2), out Mock<IPaginationButton> button);
            await QuoteSearchPagination.HandleClickAsync(click, backlog);

            button.Verify(b => b.DeferAsync(), Times.Once);
            button.Verify(b => b.ModifyListInPlaceAsync(
                "Quotes matching 'common' (page 2 of 2):" + Environment.NewLine + "#10: common text 11" + Environment.NewLine + "#11: common text 12",
                It.IsAny<MessageComponent>()), Times.Once);
            button.Verify(b => b.SendEphemeralAsync(It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task Search_Ranks_Relevance_Across_The_Page_Boundary()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            for (int number = 0; number < 9; number++)
                await harness.Execute(quote, $"!quote dog number {number}");
            await harness.Execute(quote, "!quote a dot there");
            await harness.Execute(quote, "!quote a hog here");
            await harness.Execute(quote, "!quote a cot everywhere");

            var backlog = new QuoteSearchBacklog();
            await harness.Execute(new SearchQuotesCommand(backlog), "!searchquotes dog");

            var click = CreatePaginationClick(AuthorId, QuoteSearchPagination.CustomId(AuthorId, 0, 2), out Mock<IPaginationButton> button);
            await QuoteSearchPagination.HandleClickAsync(click, backlog);

            button.Verify(b => b.ModifyListInPlaceAsync(
                "Quotes matching 'dog' (page 2 of 2):" + Environment.NewLine + "#10: a hog here" + Environment.NewLine + "#11: a cot everywhere",
                It.IsAny<MessageComponent>()), Times.Once,
                "Distance ties break by quote number and the distance-2 match ranks last, across the page boundary");
        }

        [TestMethod]
        public async Task Search_Button_Click_By_A_Foreign_User_Or_For_A_Stale_Search_Is_Declined()
        {
            var harness = Harness();
            var backlog = new QuoteSearchBacklog();
            await harness.Execute(new QuoteCommand(), "!quote one");
            await harness.Execute(new QuoteCommand(), "!quote two");
            await harness.Execute(new SearchQuotesCommand(backlog), "!searchquotes one");

            var foreign = CreatePaginationClick(clickerId: 300, customId: QuoteSearchPagination.CustomId(AuthorId, 0, 1), out Mock<IPaginationButton> foreignClick);
            await QuoteSearchPagination.HandleClickAsync(foreign, backlog);

            foreignClick.Verify(b => b.SendEphemeralAsync(QuoteSearchPagination.ForeignClickerReply), Times.Once);
            foreignClick.Verify(b => b.ModifyListInPlaceAsync(It.IsAny<string>(), It.IsAny<MessageComponent>()), Times.Never);

            // A newer search replaces the backlog entry: the first search's buttons are stale.
            await harness.Execute(new SearchQuotesCommand(backlog), "!searchquotes two");

            var stale = CreatePaginationClick(AuthorId, QuoteSearchPagination.CustomId(AuthorId, 0, 1), out Mock<IPaginationButton> staleClick);
            await QuoteSearchPagination.HandleClickAsync(stale, backlog);

            staleClick.Verify(b => b.SendEphemeralAsync(QuoteSearchPagination.UnavailableSearchReply), Times.Once);
            staleClick.Verify(b => b.ModifyListInPlaceAsync(It.IsAny<string>(), It.IsAny<MessageComponent>()), Times.Never);
        }

        [TestMethod]
        public async Task Search_Trims_The_Reply_To_The_Message_Limit()
        {
            var harness = Harness();
            for (int number = 0; number < 10; number++)
                harness.Database.Quotes.Add(new DatabaseContext.Quote { GuildId = GuildId, Number = number, Text = $"{new string('x', 240)} common {number}" });
            harness.Database.SaveChanges();

            // Ten matches at ~250 chars exceed Discord's message limit; the reply is trimmed
            // instead of failing delivery.
            var replies = await harness.Execute(Search(), "!searchquotes common");

            Assert.AreEqual(1, replies.Count);
            Assert.IsTrue(replies[0].Length <= 2000, $"The reply must fit a Discord message, got {replies[0].Length} chars");
            Assert.IsTrue(replies[0].StartsWith("Quotes matching 'common'"), "The reply must start with the search header");
            Assert.IsTrue(replies[0].EndsWith("…"), "A trimmed reply must end with an ellipsis");
        }

        [TestMethod]
        public async Task Search_Without_Matches_And_Without_Text()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote something else");

            CollectionAssert.AreEqual(
                new List<string> { "No quotes match 'missing' in this server" },
                await harness.Execute(Search(), "!searchquotes missing"));

            var empty = await harness.Execute(Search(), "!searchquotes");
            Assert.AreEqual(1, empty.Count);
            StringAssert.Contains(empty[0], "search text is missing");
        }

        [TestMethod]
        public async Task Search_AMisspelledTerm_FindsItsQuote()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote a big dog barks");

            CollectionAssert.AreEqual(
                new List<string> { "Quotes matching 'dg':" + Environment.NewLine + "#0: a big dog barks" },
                await harness.Execute(Search(), "!searchquotes dg"),
                "A near miss of a word (edit distance 1) matches the quote.");
        }

        [TestMethod]
        public async Task Search_SubstringMatches_RankAheadOfFuzzyOnes()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote a bat flies");
            await harness.Execute(new QuoteCommand(), "!quote concatenate this");

            var replies = await harness.Execute(Search(), "!searchquotes cat");

            Assert.AreEqual(1, replies.Count);
            Assert.AreEqual("Quotes matching 'cat':" + Environment.NewLine + "#1: concatenate this" + Environment.NewLine + "#0: a bat flies", replies[0],
                "The substring match ranks ahead of the closer fuzzy match.");
        }

        [TestMethod]
        public async Task Search_FuzzyMatches_BreakDistanceTies_ByQuoteNumber()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote the cap fits");
            await harness.Execute(new QuoteCommand(), "!quote a bat flies");

            var replies = await harness.Execute(Search(), "!searchquotes cat");

            Assert.AreEqual(1, replies.Count);
            Assert.AreEqual("Quotes matching 'cat':" + Environment.NewLine + "#0: the cap fits" + Environment.NewLine + "#1: a bat flies", replies[0],
                "An equal word distance is ordered by quote number.");
        }

        [TestMethod]
        public async Task Search_AFuzzyMiss_KeepsTheNoMatchReply()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote something else");

            CollectionAssert.AreEqual(
                new List<string> { "No quotes match 'zzqqxx' in this server" },
                await harness.Execute(Search(), "!searchquotes zzqqxx"));
        }

        [TestMethod]
        public async Task Remove_Unknown_Or_Malformed_Number_Produces_In_Channel_Errors()
        {
            var harness = Harness();

            var unknown = await harness.Execute(new RemoveQuoteCommand(), "!removequote 7");
            StringAssert.Contains(unknown[0], "no quote with number 7");

            var malformed = await harness.Execute(new RemoveQuoteCommand(), "!removequote soon");
            StringAssert.Contains(malformed[0], "Could not parse a quote number from 'soon'");

            Assert.AreEqual(0, harness.Database.Quotes.Count());
        }

        [TestMethod]
        public void Default_Tiers_Are_Everyone_For_Quote_And_Search_Moderator_For_Remove()
        {
            Assert.AreEqual(PermissionTier.Everyone, new QuoteCommand().DefaultPermissionTier);
            Assert.AreEqual(PermissionTier.Everyone, Search().DefaultPermissionTier);
            Assert.AreEqual(PermissionTier.Moderator, new RemoveQuoteCommand().DefaultPermissionTier);
        }

        [TestMethod]
        public async Task Fresh_Guild_User_Cannot_Remove_Quotes_But_Can_Use_Them()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var user = DiscordTestDoubles.CreateUser(AuthorId).Object;

            var removeCheck = await permissionsService.CheckUserPermissions(user.Id, GuildId, TierOverrides.None, new RemoveQuoteCommand(), database);
            var quoteCheck = await permissionsService.CheckUserPermissions(user.Id, GuildId, TierOverrides.None, new QuoteCommand(), database);

            Assert.IsFalse(removeCheck.IsAllowed, "removequote defaults to moderator");
            Assert.IsTrue(quoteCheck.IsAllowed, "quote defaults to everyone");
        }

        [TestMethod]
        public async Task CommandPermission_Can_Adjust_RemoveQuote_Tier_At_Runtime()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();

            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = GuildId, CommandName = "removequote", PermissionTier = PermissionTier.Everyone });
            database.SaveChanges();

            var check = await permissionsService.CheckUserPermissions(AuthorId, GuildId, TierOverrides.None, new RemoveQuoteCommand(), database);
            Assert.IsTrue(check.IsAllowed, "A CommandMetaData override must adjust the required tier");
        }

        [TestMethod]
        public async Task All_Quote_Commands_Decline_In_Direct_Messages()
        {
            var harness = Harness();

            foreach (ICommand command in new ICommand[] { new QuoteCommand(), Search(), new RemoveQuoteCommand(), new ImportQuotesCommand(new FakeAttachmentDownloader()) })
            {
                var replies = await harness.Execute(command, $"!{command.Name}", guildId: null);

                Assert.AreEqual(1, replies.Count);
                StringAssert.Contains(replies[0], "only available in servers");
            }

            Assert.AreEqual(0, harness.Database.Quotes.Count(), "No quotes may be stored from a DM");
        }

        [TestMethod]
        public async Task Import_Appends_In_File_Order_With_Continued_Numbering_And_Legacy_Dates()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote one");
            var importTimestamp = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

            // The old bot's file shape, including its savingRequired field (ignored) and an
            // entry without a date (falls back to the import message's timestamp).
            var downloader = new FakeAttachmentDownloader
            {
                Content = "{\"quotes\":[{\"quote\":\"legacy a\",\"date\":\"2020-05-06T07:08:09Z\"},{\"quote\":\"legacy b\"}],\"savingRequired\":false}"
            };
            var attachment = DiscordTestDoubles.CreateAttachment(url: "https://cdn.example.test/quotes.json");

            var replies = await harness.Execute(new ImportQuotesCommand(downloader), "!importquotes", timestamp: importTimestamp, attachment: attachment);

            CollectionAssert.AreEqual(new List<string> { "Imported 2 quotes, assigned numbers #1 to #2" }, replies);
            CollectionAssert.AreEqual(new List<string> { "https://cdn.example.test/quotes.json" }, downloader.RequestedUrls);

            var quotes = harness.Database.Quotes.OrderBy(quote => quote.Number).ToList();
            Assert.AreEqual(3, quotes.Count);
            Assert.AreEqual(("one", 0), (quotes[0].Text, quotes[0].Number));
            Assert.AreEqual(("legacy a", 1), (quotes[1].Text, quotes[1].Number));
            Assert.AreEqual(("legacy b", 2), (quotes[2].Text, quotes[2].Number));
            Assert.AreEqual(AuthorId, quotes[1].AddedByDiscordId, "The importing user is recorded as the adder");
            Assert.AreEqual(new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc), quotes[1].CreatedAt, "The legacy date must survive the migration");
            Assert.AreEqual(importTimestamp.UtcDateTime, quotes[2].CreatedAt, "An entry without a date falls back to the import message's timestamp");
        }

        [TestMethod]
        public async Task Import_Failures_Produce_In_Channel_Errors_And_Store_Nothing()
        {
            // (name, attachment, downloader, expected fragment) — each failure path must reply
            var cases = new (string Name, IAttachment? Attachment, FakeAttachmentDownloader Downloader, string Expected)[]
            {
                ("no attachment", null, new FakeAttachmentDownloader { Content = "{\"quotes\":[{\"quote\":\"a\"}]}" }, "Attach the old bot's quoteSettings.json"),
                ("download failure", DiscordTestDoubles.CreateAttachment(), new FakeAttachmentDownloader { Failure = new HttpRequestException() }, "could not be downloaded"),
                ("malformed json", DiscordTestDoubles.CreateAttachment(), new FakeAttachmentDownloader { Content = "not json at all" }, "could not be parsed"),
                ("empty quotes list", DiscordTestDoubles.CreateAttachment(), new FakeAttachmentDownloader { Content = "{\"quotes\":[]}" }, "contains no quotes"),
                ("empty entry", DiscordTestDoubles.CreateAttachment(), new FakeAttachmentDownloader { Content = "{\"quotes\":[{\"quote\":\"valid first\"},{\"quote\":\"   \"}]}" }, "Quote 2 in the file is empty"),
                ("over-long entry", DiscordTestDoubles.CreateAttachment(), new FakeAttachmentDownloader { Content = $"{{\"quotes\":[{{\"quote\":\"valid first\"}},{{\"quote\":\"{new string('z', 2001)}\"}}]}}" }, "longer than 2000 characters"),
            };

            foreach (var testCase in cases)
            {
                var harness = Harness();

                var replies = await harness.Execute(new ImportQuotesCommand(testCase.Downloader), "!importquotes", attachment: testCase.Attachment);

                Assert.AreEqual(1, replies.Count, $"{testCase.Name}: expected exactly one in-channel reply");
                StringAssert.Contains(replies[0], testCase.Expected, testCase.Name);
                Assert.AreEqual(0, harness.Database.Quotes.Count(), $"{testCase.Name}: nothing may be stored on a failed import");
            }
        }

        [TestMethod]
        public async Task Import_Accepts_Entries_Up_To_2000_Characters()
        {
            var harness = Harness();
            string boundaryText = new string('y', 2000);
            var downloader = new FakeAttachmentDownloader { Content = $"{{\"quotes\":[{{\"quote\":\"{boundaryText}\"}}]}}" };

            var replies = await harness.Execute(new ImportQuotesCommand(downloader), "!importquotes", attachment: DiscordTestDoubles.CreateAttachment());

            CollectionAssert.AreEqual(new List<string> { "Imported 1 quotes, assigned numbers #0 to #0" }, replies);
            Assert.AreEqual(boundaryText, harness.Database.Quotes.Single().Text);
        }

        [TestMethod]
        public void Import_Default_Tier_Is_BotOwner()
        {
            Assert.AreEqual(PermissionTier.BotOwner, new ImportQuotesCommand().DefaultPermissionTier);
        }

        [TestMethod]
        public async Task Import_Is_Allowed_Only_For_The_Configured_Bot_Owner()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var command = new ImportQuotesCommand();

            // The guild owner tops out at tier 3 — below the command's tier-4 default.
            var guildOwnerOverrides = new TierOverrides { GuildOwnerId = 999 };
            Assert.IsFalse((await permissionsService.CheckUserPermissions(999, GuildId, guildOwnerOverrides, command, database)).IsAllowed, "A guild owner (tier 3) must not run the import");

            var botOwnerOverrides = new TierOverrides { BotOwnerId = 250 };
            Assert.IsTrue((await permissionsService.CheckUserPermissions(250, GuildId, botOwnerOverrides, command, database)).IsAllowed, "The configured bot owner must be allowed");
        }
    }
}
