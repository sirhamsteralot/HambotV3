using Discord;
using HambotV2.Commands;
using HambotV2.Commands.QuoteCommands;
using HambotV2.Connections;
using HambotV2.Maintenance;
using HambotV2.QuoteOfTheDay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestQuoteOfTheDay
    {
        private const ulong GuildId = 11;
        private const ulong OtherGuildId = 12;
        private const ulong ChannelId = 700;
        private const ulong OtherChannelId = 701;
        private const ulong GuildOwnerId = 999;

        private static readonly DateTimeOffset Day1Noon = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Day1Midnight = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Day2Midnight = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public void Schedule_Anchors_Boundaries_To_The_Configured_Utc_Time()
        {
            var anchor = new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero);
            var daily = new QuoteOfTheDaySchedule(ChannelId, TimeSpan.FromDays(1), anchor);

            // A daily interval publishes at the same UTC time every day.
            Assert.AreEqual(anchor, daily.CurrentBoundary(anchor.AddMinutes(1)));
            Assert.AreEqual(anchor.AddDays(1), daily.NextBoundary(anchor.AddMinutes(1)));
            Assert.AreEqual(anchor.AddDays(2), daily.NextBoundary(anchor.AddDays(1).AddMinutes(1)));

            // An anchor in the future — the "in 5h" form: with a daily interval the boundaries
            // before the anchor are a day apart, so the next one after "now" is the anchor
            // itself, and the first publication lands at exactly that time.
            var fiveHoursAhead = new QuoteOfTheDaySchedule(ChannelId, TimeSpan.FromDays(1), anchor.AddHours(5));
            Assert.AreEqual(anchor.AddHours(5) - TimeSpan.FromDays(1), fiveHoursAhead.CurrentBoundary(anchor));
            Assert.AreEqual(anchor.AddHours(5), fiveHoursAhead.NextBoundary(anchor));
        }

        [TestMethod]
        public void Schedule_Sub_Daily_Intervals_Align_Boundaries_To_The_Anchor()
        {
            var anchor = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);
            var tenMinutes = new QuoteOfTheDaySchedule(ChannelId, TimeSpan.FromMinutes(10), anchor);
            Assert.AreEqual(new DateTimeOffset(2026, 1, 1, 11, 55, 0, TimeSpan.Zero),
                tenMinutes.CurrentBoundary(new DateTimeOffset(2026, 1, 1, 12, 3, 0, TimeSpan.Zero)));
            Assert.AreEqual(new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero),
                tenMinutes.NextBoundary(new DateTimeOffset(2026, 1, 1, 12, 3, 0, TimeSpan.Zero)));

            var hourly = new QuoteOfTheDaySchedule(ChannelId, TimeSpan.FromHours(1), anchor + TimeSpan.FromHours(9) + TimeSpan.FromMinutes(25));
            Assert.AreEqual(new DateTimeOffset(2026, 1, 1, 11, 30, 0, TimeSpan.Zero),
                hourly.CurrentBoundary(new DateTimeOffset(2026, 1, 1, 12, 3, 0, TimeSpan.Zero)));
            Assert.AreEqual(new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero),
                hourly.NextBoundary(new DateTimeOffset(2026, 1, 1, 12, 3, 0, TimeSpan.Zero)));
        }

        [TestMethod]
        public void Template_Validation_Rejects_Empty_Overlong_And_Placeholder_Free_Input()
        {
            Assert.IsFalse(QuoteOfTheDaySchedule.IsValidTemplate(""), "An empty template is invalid");
            Assert.IsFalse(QuoteOfTheDaySchedule.IsValidTemplate(new string('a', 501) + "{quote}"), "A template over 500 characters is invalid");
            Assert.IsFalse(QuoteOfTheDaySchedule.IsValidTemplate("no placeholder here"), "A template without the {quote} placeholder is invalid");
            Assert.IsTrue(QuoteOfTheDaySchedule.IsValidTemplate(QuoteOfTheDaySchedule.DefaultTemplate));
            Assert.IsTrue(QuoteOfTheDaySchedule.IsValidTemplate(new string('a', 493) + "{quote}"), "A template of exactly 500 characters is valid");
        }

        [TestMethod]
        public void Rendering_Replaces_Every_Placeholder_With_The_Number_Prefixed_Quote_And_Trims_At_The_Limit()
        {
            var quote = new DatabaseContext.Quote { Number = 3, Text = "hello" };

            Assert.AreEqual("#3: hello / #3: hello", QuoteOfTheDaySchedule.Render("{quote} / {quote}", quote));

            string rendered = QuoteOfTheDaySchedule.Render("{quote}",
                new DatabaseContext.Quote { Number = 0, Text = new string('a', 2500) });
            Assert.AreEqual(2000, rendered.Length, "The rendered message is capped at Discord's message limit");
            Assert.IsTrue(rendered.EndsWith('…'), "The cap trims with a trailing ellipsis");
        }

        [TestMethod]
        public void Pipeline_Resolves_QuoteOfTheDay_With_Admin_Default_Tier()
        {
            var commandService = new CommandService();
            commandService.RegisterCommands();

            Assert.IsTrue(commandService.TryGetCommand("!quoteoftheday", "!", out ICommand? command));
            Assert.AreEqual("quoteoftheday", command.Name);
            Assert.AreEqual(PermissionTier.Admin, command.DefaultPermissionTier);
        }

        [TestMethod]
        public async Task Command_Declines_In_Direct_Messages()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandAsync(database, new QuoteOfTheDayCommand(), "!quoteoftheday", guildId: null);

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "only available in servers");
            Assert.AreEqual(0, database.GuildSettings.Count(), "No guild state may be written from a direct message");
        }

        [TestMethod]
        public async Task Command_Show_Reports_The_Feature_Off_Until_A_Channel_Is_Set()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandAsync(database, new QuoteOfTheDayCommand(), "!quoteoftheday");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "off");
        }

        [TestMethod]
        public async Task Command_Channel_Sets_Clears_And_Persists_The_Target_Channel()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var command = new QuoteOfTheDayCommand();

            await ExecuteCommandAsync(database, command, "!quoteoftheday channel <#42>");
            Assert.AreEqual(42UL, database.GuildSettings.Single().QuoteOfTheDayChannelId);

            var show = await ExecuteCommandAsync(database, command, "!quoteoftheday");
            StringAssert.Contains(show[0], "<#42>", "The show view names the configured channel");

            await ExecuteCommandAsync(database, command, "!quoteoftheday channel clear");
            Assert.IsNull(database.GuildSettings.Single().QuoteOfTheDayChannelId);

            var showAfterClear = await ExecuteCommandAsync(database, command, "!quoteoftheday");
            StringAssert.Contains(showAfterClear[0], "off");
        }

        [TestMethod]
        public async Task Command_Channel_Rejects_Input_That_Is_Not_A_Channel()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandAsync(database, new QuoteOfTheDayCommand(), "!quoteoftheday channel banana");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "does not look like a channel");
            Assert.AreEqual(0, database.GuildSettings.Count(), "Nothing may be stored for invalid input");
        }

        [TestMethod]
        public async Task Command_Template_Stores_Valid_Input_And_Rejects_Invalid_Input()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var command = new QuoteOfTheDayCommand();
            await ExecuteCommandAsync(database, command, "!quoteoftheday channel <#42>");

            var stored = await ExecuteCommandAsync(database, command, "!quoteoftheday template this day rocks: {quote}");
            Assert.AreEqual(1, stored.Count);
            Assert.AreEqual("this day rocks: {quote}", database.GuildSettings.Single().QuoteOfTheDayTemplate);

            var missingPlaceholder = await ExecuteCommandAsync(database, command, "!quoteoftheday template plain text");
            StringAssert.Contains(missingPlaceholder[0], "placeholder");
            Assert.AreEqual("this day rocks: {quote}", database.GuildSettings.Single().QuoteOfTheDayTemplate);

            var tooLong = await ExecuteCommandAsync(database, command, "!quoteoftheday template " + new string('a', 501) + " {quote}");
            StringAssert.Contains(tooLong[0], "characters");
            Assert.AreEqual("this day rocks: {quote}", database.GuildSettings.Single().QuoteOfTheDayTemplate);
        }

        [TestMethod]
        [DataRow("5h 1d", 300, 1440)]
        [DataRow("30m 1h", 30, 60)]
        [DataRow("2d 1 week", 2880, 10080)]
        [DataRow("30m 2d 12h", 30, 3600)]
        public async Task Command_Schedule_Accepts_Due_Time_And_Interval_Duration_Forms(string arguments, int expectedDueMinutes, int expectedIntervalMinutes)
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var invokedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

            await ExecuteCommandAsync(database, new QuoteOfTheDayCommand(), $"!quoteoftheday {arguments}", timestamp: invokedAt);

            var setting = database.GuildSettings.Single();
            Assert.AreEqual(invokedAt + TimeSpan.FromMinutes(expectedDueMinutes), setting.QuoteOfTheDayAnchorUtc,
                "The due time anchors the schedule to the end of the duration from the invocation");
            Assert.AreEqual(expectedIntervalMinutes, setting.QuoteOfTheDayIntervalMinutes);
        }

        [TestMethod]
        public async Task Command_Schedule_Enforces_The_Deployment_Minimum()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var command = new QuoteOfTheDayCommand(new QuoteOfTheDaySettings { MinInterval = TimeSpan.FromMinutes(5) });

            var replies = await ExecuteCommandAsync(database, command, "!quoteoftheday 5h 1m");

            StringAssert.Contains(replies[0], "at least");
            Assert.AreEqual(0, database.GuildSettings.Count(), "Nothing may be stored below the minimum");
        }

        [TestMethod]
        [DataRow("5h 90s")]
        [DataRow("banana 1d")]
        [DataRow("5h")]
        [DataRow("5h banana")]
        public async Task Command_Schedule_Rejects_Unparsable_And_Sub_Minute_Input(string arguments)
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandAsync(database, new QuoteOfTheDayCommand(), $"!quoteoftheday {arguments}");

            Assert.AreEqual(1, replies.Count);
            Assert.AreEqual(0, database.GuildSettings.Count(), "Nothing may be stored for invalid input");
        }

        [TestMethod]
        public async Task Command_Show_Reports_The_Configuration_And_Next_Due_Time()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var command = new QuoteOfTheDayCommand();
            var invokedAt = new DateTimeOffset(2026, 1, 1, 12, 3, 0, TimeSpan.Zero);
            await ExecuteCommandAsync(database, command, "!quoteoftheday channel <#42>");
            await ExecuteCommandAsync(database, command, "!quoteoftheday 27m 1h", timestamp: invokedAt);
            await ExecuteCommandAsync(database, command, "!quoteoftheday template best of the server: {quote}");

            var replies = await ExecuteCommandAsync(database, command, "!quoteoftheday", timestamp: invokedAt);

            StringAssert.Contains(replies[0], "<#42>");
            StringAssert.Contains(replies[0], "1h");
            StringAssert.Contains(replies[0], "12:30 UTC");
            StringAssert.Contains(replies[0], "next due 2026-01-01 12:30", "The next boundary is the anchor — the first publication lands at the due time");
            StringAssert.Contains(replies[0], "best of the server: {quote}");
        }

        [TestMethod]
        public async Task Command_Replies_Usage_For_Input_That_Is_No_Subcommand_Or_Schedule()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandAsync(database, new QuoteOfTheDayCommand(), "!quoteoftheday frobnicate");

            StringAssert.Contains(replies[0], "does not look like a schedule");
        }

        [TestMethod]
        public async Task Publication_Baselines_First_Then_Publishes_Once_Per_Boundary()
        {
            using var harness = new PublicationHarness();
            await harness.SeedGuildAsync(GuildId, ChannelId, quotes: ["first", "second", "third"]);
            var service = harness.Service;

            await service.RunPassAsync(Day1Noon);
            Assert.AreEqual(0, harness.Sender.Sent.Count, "The first pass baselines; nothing publishes before the next boundary");

            await service.RunPassAsync(Day2Midnight.AddSeconds(1));
            Assert.AreEqual(1, harness.Sender.Sent.Count, "The first publication happens at the next schedule boundary");
            StringAssert.StartsWith(harness.Sender.Sent[0].Message, "quote of the day: #", "The default template carries the number-prefixed quote");

            await service.RunPassAsync(Day2Midnight.AddMinutes(5));
            Assert.AreEqual(1, harness.Sender.Sent.Count, "At most one publication per boundary");

            await service.RunPassAsync(Day2Midnight.AddDays(1));
            Assert.AreEqual(2, harness.Sender.Sent.Count, "The next boundary publishes again");
        }

        [TestMethod]
        public async Task Publication_Skips_Boundaries_Missed_While_The_Bot_Was_Down()
        {
            using var harness = new PublicationHarness();
            await harness.SeedGuildAsync(GuildId, ChannelId, quotes: ["first", "second", "third"]);
            var service = harness.Service;

            // A fresh in-process state stands in for a restart several boundaries later: the
            // boundaries that passed meanwhile are skipped, never caught up.
            await service.RunPassAsync(Day1Midnight.AddDays(3).AddHours(12));
            Assert.AreEqual(0, harness.Sender.Sent.Count, "No catch-up burst after downtime");

            await service.RunPassAsync(Day1Midnight.AddDays(4));
            Assert.AreEqual(1, harness.Sender.Sent.Count, "The next boundary publishes exactly once");
        }

        [TestMethod]
        public async Task Publication_Failed_Post_Skips_Its_Boundary_Without_Shifting_The_Schedule()
        {
            using var harness = new PublicationHarness();
            await harness.SeedGuildAsync(GuildId, ChannelId, quotes: ["first", "second"]);
            var service = harness.Service;
            await service.RunPassAsync(Day1Noon);

            harness.Sender.FailSends = true;
            await service.RunPassAsync(Day2Midnight.AddSeconds(1));
            Assert.AreEqual(0, harness.Sender.Sent.Count, "The failed post publishes nothing");

            harness.Sender.FailSends = false;
            await service.RunPassAsync(Day2Midnight.AddMinutes(5));
            Assert.AreEqual(0, harness.Sender.Sent.Count, "A failed boundary is skipped, not retried");

            await service.RunPassAsync(Day2Midnight.AddDays(1));
            Assert.AreEqual(1, harness.Sender.Sent.Count, "The next publication is attempted at the next boundary — the feature stays on");
        }

        [TestMethod]
        public async Task Publication_Skips_The_Boundary_Of_A_Quote_Less_Guild()
        {
            using var harness = new PublicationHarness();
            await harness.SeedGuildAsync(GuildId, ChannelId);
            var service = harness.Service;
            await service.RunPassAsync(Day1Noon);

            await service.RunPassAsync(Day2Midnight.AddSeconds(1));
            Assert.AreEqual(0, harness.Sender.Sent.Count, "A guild without quotes skips its boundary without posting");

            await harness.AddQuoteAsync(GuildId, "fresh");
            await service.RunPassAsync(Day2Midnight.AddDays(1));
            Assert.AreEqual(1, harness.Sender.Sent.Count, "The next boundary publishes once a quote exists");
        }

        [TestMethod]
        public async Task Publication_Pauses_During_Maintenance_And_Skips_Its_Boundaries()
        {
            using var harness = new PublicationHarness();
            await harness.SeedGuildAsync(GuildId, ChannelId, quotes: ["first", "second"]);
            var service = harness.Service;
            await service.RunPassAsync(Day1Noon);

            harness.Maintenance.Enter();
            await service.RunPassAsync(Day2Midnight.AddSeconds(1));
            Assert.AreEqual(0, harness.Sender.Sent.Count, "No publication while maintenance mode is active");

            harness.Maintenance.Exit();
            await service.RunPassAsync(Day2Midnight.AddMinutes(5));
            Assert.AreEqual(0, harness.Sender.Sent.Count, "The boundary that passed during maintenance is skipped, not caught up");

            await service.RunPassAsync(Day2Midnight.AddDays(1));
            Assert.AreEqual(1, harness.Sender.Sent.Count, "The next publication happens at the next boundary after maintenance ends");
        }

        [TestMethod]
        public async Task Publication_Isolates_Guilds_By_Their_Own_Schedules()
        {
            using var harness = new PublicationHarness();
            await harness.SeedGuildAsync(GuildId, ChannelId, quotes: ["guild a"]);
            await harness.SeedGuildAsync(OtherGuildId, OtherChannelId, anchorUtc: Day2Midnight.AddHours(12), quotes: ["guild b"]);
            var service = harness.Service;

            await service.RunPassAsync(Day1Noon);

            await service.RunPassAsync(Day2Midnight.AddSeconds(1));
            Assert.AreEqual(1, harness.Sender.Sent.Count);
            Assert.AreEqual(ChannelId, harness.Sender.Sent[0].ChannelId, "Only the guild whose boundary is due publishes");

            await service.RunPassAsync(Day2Midnight.AddHours(12).AddSeconds(1));
            Assert.AreEqual(2, harness.Sender.Sent.Count);
            Assert.AreEqual(OtherChannelId, harness.Sender.Sent[1].ChannelId, "The other guild publishes at its own anchor");
        }

        [TestMethod]
        public async Task Publication_Takes_Configuration_Changes_Effect_At_The_Next_Boundary()
        {
            using var harness = new PublicationHarness();
            await harness.SeedGuildAsync(GuildId, ChannelId, anchorUtc: Day1Midnight.AddHours(9), quotes: ["first", "second"]);
            var service = harness.Service;

            var firstBoundary = Day1Midnight.AddHours(9);
            await service.RunPassAsync(firstBoundary.AddHours(-1));
            Assert.AreEqual(0, harness.Sender.Sent.Count, "The pass before the boundary publishes nothing");

            await service.RunPassAsync(firstBoundary.AddHours(1));
            Assert.AreEqual(1, harness.Sender.Sent.Count, "The anchor boundary published");

            // The anchor moves from 09:00 to 15:00 — no mid-boundary publication of the change.
            await harness.ChangeAnchorAsync(GuildId, firstBoundary.AddHours(6));
            await service.RunPassAsync(firstBoundary.AddHours(5));
            Assert.AreEqual(1, harness.Sender.Sent.Count, "A configuration change never triggers a mid-boundary publication");

            await service.RunPassAsync(firstBoundary.AddHours(6).AddSeconds(1));
            Assert.AreEqual(2, harness.Sender.Sent.Count, "The new schedule publishes at its next boundary");
        }

        [TestMethod]
        public async Task Publication_Uses_A_Changed_Template_At_The_Next_Boundary()
        {
            using var harness = new PublicationHarness();
            await harness.SeedGuildAsync(GuildId, ChannelId, quotes: ["first", "second"]);
            var service = harness.Service;
            await service.RunPassAsync(Day1Noon);

            await harness.ChangeTemplateAsync(GuildId, "listen to this one: {quote}");
            await service.RunPassAsync(Day2Midnight.AddSeconds(1));

            Assert.AreEqual(1, harness.Sender.Sent.Count, "A template change leaves the schedule untouched — one publication at the boundary");
            StringAssert.StartsWith(harness.Sender.Sent[0].Message, "listen to this one: #");
        }

        private static async Task<List<string>> ExecuteCommandAsync(
            DatabaseContext database, QuoteOfTheDayCommand command, string content, ulong? guildId = GuildId, DateTimeOffset? timestamp = null)
        {
            var sentMessages = new List<string>();
            var message = guildId is ulong guild
                ? DiscordTestDoubles.CreateGuildMessage(content, 100, GuildOwnerId, sentMessages, guild, timestamp)
                : DiscordTestDoubles.CreateDirectMessage(content, 100, sentMessages, timestamp);
            var context = DiscordTestDoubles.CreateContext(database, guildId: guildId);

            await command.ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        private sealed class RecordingSender : IQuoteOfTheDayChannelSender
        {
            public List<(ulong ChannelId, string Message)> Sent { get; } = new();

            public bool FailSends { get; set; }

            public Task SendAsync(ulong channelId, string message, CancellationToken cancellationToken)
            {
                if (FailSends)
                    throw new InvalidOperationException("simulated Discord failure");

                Sent.Add((channelId, message));
                return Task.CompletedTask;
            }
        }

        private sealed class PublicationHarness : IDisposable
        {
            private readonly string _storeName = Guid.NewGuid().ToString();
            private readonly ServiceProvider _provider;

            public RecordingSender Sender { get; } = new();
            public MaintenanceMode Maintenance { get; } = new();
            public QuoteOfTheDayPublicationService Service { get; }

            public PublicationHarness()
            {
                var services = new ServiceCollection();
                services.AddTransient<IDatabaseContext>(_ => CreateDatabase());
                _provider = services.BuildServiceProvider();

                Service = new QuoteOfTheDayPublicationService(
                    _provider.GetRequiredService<IServiceScopeFactory>(),
                    Sender,
                    TimeProvider.System,
                    NullLogger<QuoteOfTheDayPublicationService>.Instance,
                    Maintenance);
            }

            public DatabaseContext CreateDatabase()
            {
                return new DatabaseContext(
                    new DbContextOptionsBuilder<DatabaseContext>()
                        .UseInMemoryDatabase(_storeName)
                        .Options);
            }

            public async Task SeedGuildAsync(
                ulong guildId, ulong channelId, int? intervalMinutes = null, DateTimeOffset? anchorUtc = null, string[]? quotes = null)
            {
                using var database = CreateDatabase();
                database.GuildSettings.Add(new DatabaseContext.GuildSetting
                {
                    GuildId = guildId,
                    QuoteOfTheDayChannelId = channelId,
                    QuoteOfTheDayIntervalMinutes = intervalMinutes,
                    QuoteOfTheDayAnchorUtc = anchorUtc
                });

                int number = 0;
                foreach (string text in quotes ?? [])
                {
                    database.Quotes.Add(new DatabaseContext.Quote
                    {
                        GuildId = guildId,
                        Number = number++,
                        Text = text,
                        AddedByDiscordId = 1,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await database.SaveChangesAsync();
            }

            public async Task AddQuoteAsync(ulong guildId, string text)
            {
                using var database = CreateDatabase();
                int nextNumber = (await database.Quotes.Where(quote => quote.GuildId == guildId).MaxAsync(quote => (int?)quote.Number) ?? -1) + 1;
                database.Quotes.Add(new DatabaseContext.Quote { GuildId = guildId, Number = nextNumber, Text = text, AddedByDiscordId = 1, CreatedAt = DateTime.UtcNow });
                await database.SaveChangesAsync();
            }

            public async Task ChangeAnchorAsync(ulong guildId, DateTimeOffset anchorUtc)
            {
                using var database = CreateDatabase();
                var setting = await database.GuildSettings.SingleAsync(item => item.GuildId == guildId);
                setting.QuoteOfTheDayAnchorUtc = anchorUtc;
                await database.SaveChangesAsync();
            }

            public async Task ChangeTemplateAsync(ulong guildId, string template)
            {
                using var database = CreateDatabase();
                var setting = await database.GuildSettings.SingleAsync(item => item.GuildId == guildId);
                setting.QuoteOfTheDayTemplate = template;
                await database.SaveChangesAsync();
            }

            public void Dispose()
            {
                _provider.Dispose();
            }
        }
    }
}
