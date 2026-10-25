using Discord;
using HambotV2.Commands;
using HambotV2.Commands.QuoteCommands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>
    /// The quotes feature (FR-42–FR-50, NFR-14): per-guild storage with sequential numbering,
    /// random/by-number/add-by-text fetching, search, removal, and pipeline behavior.
    /// </summary>
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

            public async Task<List<string>> Execute(ICommand command, string content, ulong? guildId = GuildId, ulong authorId = AuthorId, DateTimeOffset? timestamp = null, IAttachment? attachment = null)
            {
                var sentMessages = new List<string>();
                var message = guildId is ulong guild
                    ? DiscordTestDoubles.CreateGuildMessage(content, authorId, 999, sentMessages, guild, timestamp, attachment)
                    : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);
                var context = DiscordTestDoubles.CreateContext(Database, guildId: guildId);

                await command.ExecuteCommand(message.Object, context);
                return sentMessages;
            }
        }

        /// <summary>Downloader double for !importquotes: returns canned content or throws (FR-72).</summary>
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

        // --- Lifecycle & sequential numbering (FR-43, NFR-14) ---

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
            Assert.AreEqual(AuthorId, quotes[0].AddedByDiscordId, "The adder's Discord id must be recorded (FR-42)");
            Assert.AreEqual(timestamp.UtcDateTime, quotes[0].CreatedAt, "The adding message's timestamp must be recorded (FR-42)");
        }

        [TestMethod]
        public async Task Remove_Never_Renumbers_And_Top_Number_Is_Reused()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            var remove = new RemoveQuoteCommand();

            foreach (string text in new[] { "one", "two", "three" })
                await harness.Execute(quote, $"!quote {text}");

            // Removing a middle quote keeps the remaining numbers (FR-43)...
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

        // --- Per-guild isolation (FR-42, NFR-14) ---

        [TestMethod]
        public async Task Quotes_Are_Isolated_Per_Guild()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            var remove = new RemoveQuoteCommand();
            var search = new SearchQuotesCommand();

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

        // --- Random fetch and the empty-guild message (FR-44, NFR-14) ---

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

            // NFR-14 asserts "a quote from the guild", not a specific one.
            var replies = await harness.Execute(new QuoteCommand(), "!quote");

            Assert.AreEqual(1, replies.Count);
            Assert.IsTrue(
                replies[0] == "#0: one" || replies[0] == "#1: two" || replies[0] == "#2: three",
                $"Expected a number-prefixed quote of the guild, got '{replies[0]}'");
        }

        // --- Fetch by number (FR-45, NFR-14) ---

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

        // --- Add by text (FR-47, NFR-14) ---

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

            // 2000 is Discord's message limit (FR-47) — the exact boundary is accepted...
            CollectionAssert.AreEqual(new List<string> { "Added quote #0" }, await harness.Execute(new QuoteCommand(), $"!quote {boundary}"));
            Assert.AreEqual(boundary, harness.Database.Quotes.Single().Text);

            // ...and anything longer is rejected in-channel with nothing stored.
            var replies = await harness.Execute(new QuoteCommand(), $"!quote {overLimit}");
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "at most 2000 characters");
            Assert.AreEqual(1, harness.Database.Quotes.Count(), "The over-long text must not be stored (FR-47)");
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

        // --- Search (FR-46, NFR-14) ---

        [TestMethod]
        public async Task Search_Is_Case_Insensitive_Number_Prefixed_And_Ordered()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            await harness.Execute(quote, "!quote Big Dog");
            await harness.Execute(quote, "!quote small cat");
            await harness.Execute(quote, "!quote another DOG here");

            var replies = await harness.Execute(new SearchQuotesCommand(), "!searchquotes dog");

            Assert.AreEqual(1, replies.Count);
            Assert.AreEqual("#0: Big Dog\n#2: another DOG here", replies[0]);
        }

        [TestMethod]
        public async Task Search_Multi_Word_Term_Matches_The_Joined_Text()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote a big dog barks");

            CollectionAssert.AreEqual(new List<string> { "#0: a big dog barks" }, await harness.Execute(new SearchQuotesCommand(), "!searchquotes big dog"));
        }

        [TestMethod]
        public async Task Search_Is_Capped_At_Ten_Results()
        {
            var harness = Harness();
            var quote = new QuoteCommand();
            for (int number = 1; number <= 12; number++)
                await harness.Execute(quote, $"!quote common text {number}");

            var replies = await harness.Execute(new SearchQuotesCommand(), "!searchquotes common");

            Assert.AreEqual(1, replies.Count);
            Assert.AreEqual(10, replies[0].Split('\n').Length, "The result list must be capped at 10 (FR-46)");
            StringAssert.Contains(replies[0], "#9: common text 10", "Results keep the number order up to the cap");
            Assert.IsFalse(replies[0].Contains("#10"), "Quotes past the cap must not be sent");
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
            var replies = await harness.Execute(new SearchQuotesCommand(), "!searchquotes common");

            Assert.AreEqual(1, replies.Count);
            Assert.IsTrue(replies[0].Length <= 2000, $"The reply must fit a Discord message, got {replies[0].Length} chars");
            Assert.IsTrue(replies[0].StartsWith("#0: "), "The results must start with the lowest number");
            Assert.IsTrue(replies[0].EndsWith("…"), "A trimmed reply must end with an ellipsis");
        }

        [TestMethod]
        public async Task Search_Without_Matches_And_Without_Text()
        {
            var harness = Harness();
            await harness.Execute(new QuoteCommand(), "!quote something else");

            CollectionAssert.AreEqual(
                new List<string> { "No quotes match 'missing' in this server" },
                await harness.Execute(new SearchQuotesCommand(), "!searchquotes missing"));

            var empty = await harness.Execute(new SearchQuotesCommand(), "!searchquotes");
            Assert.AreEqual(1, empty.Count);
            StringAssert.Contains(empty[0], "search text is missing");
        }

        // --- Removal (FR-48, NFR-14) ---

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

        // --- Permission gating (FR-48/FR-49, NFR-14) ---

        [TestMethod]
        public void Default_Tiers_Are_Everyone_For_Quote_And_Search_Moderator_For_Remove()
        {
            Assert.AreEqual(PermissionTier.Everyone, new QuoteCommand().DefaultPermissionTier);
            Assert.AreEqual(PermissionTier.Everyone, new SearchQuotesCommand().DefaultPermissionTier);
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

            Assert.IsFalse(removeCheck.IsAllowed, "removequote defaults to moderator (FR-48)");
            Assert.IsTrue(quoteCheck.IsAllowed, "quote defaults to everyone (FR-48)");
        }

        [TestMethod]
        public async Task CommandPermission_Can_Adjust_RemoveQuote_Tier_At_Runtime()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();

            // The runtime adjustment (FR-49) is just CommandMetaData: lowering it to everyone
            // lets a fresh-guild user run the command.
            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = GuildId, CommandName = "removequote", PermissionTier = PermissionTier.Everyone });
            database.SaveChanges();

            var check = await permissionsService.CheckUserPermissions(AuthorId, GuildId, TierOverrides.None, new RemoveQuoteCommand(), database);
            Assert.IsTrue(check.IsAllowed, "A CommandMetaData override must adjust the required tier (FR-49)");
        }

        // --- DM declines (FR-50, NFR-14) ---

        [TestMethod]
        public async Task All_Quote_Commands_Decline_In_Direct_Messages()
        {
            var harness = Harness();

            foreach (ICommand command in new ICommand[] { new QuoteCommand(), new SearchQuotesCommand(), new RemoveQuoteCommand(), new ImportQuotesCommand(new FakeAttachmentDownloader()) })
            {
                var replies = await harness.Execute(command, $"!{command.Name}", guildId: null);

                Assert.AreEqual(1, replies.Count);
                StringAssert.Contains(replies[0], "only available in servers");
            }

            Assert.AreEqual(0, harness.Database.Quotes.Count(), "No quotes may be stored from a DM");
        }

        // --- Legacy import (FR-72–FR-75, NFR-14) ---

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
            Assert.AreEqual(AuthorId, quotes[1].AddedByDiscordId, "The importing user is recorded as the adder (FR-73)");
            Assert.AreEqual(new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc), quotes[1].CreatedAt, "The legacy date must survive the migration (FR-73)");
            Assert.AreEqual(importTimestamp.UtcDateTime, quotes[2].CreatedAt, "An entry without a date falls back to the import message's timestamp (FR-73)");
        }

        [TestMethod]
        public async Task Import_Failures_Produce_In_Channel_Errors_And_Store_Nothing()
        {
            // (name, attachment, downloader, expected fragment) — each failure path must reply
            // in-channel and leave the database untouched (all-or-nothing, FR-74).
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

            // Entries at the 2000-char quote limit (FR-47) import fine.
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

            // Only the configured bot owner reaches tier 4 (FR-37).
            var botOwnerOverrides = new TierOverrides { BotOwnerId = 250 };
            Assert.IsTrue((await permissionsService.CheckUserPermissions(250, GuildId, botOwnerOverrides, command, database)).IsAllowed, "The configured bot owner must be allowed");
        }
    }
}
