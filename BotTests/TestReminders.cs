using Discord;
using HambotV2.Commands;
using HambotV2.Commands.ReminderCommands;
using HambotV2.Connections;
using HambotV2.Reminders;
using HambotV2.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>
    /// The reminders feature (FR-61–FR-71, NFR-16): duration parsing across all forms, message
    /// validation, confirmation, per-user isolation and caps, list/cancel, and the delivery
    /// loop — due-vs-not-due as the (fake) clock advances, re-scheduling on earlier reminders,
    /// restart recovery, and the bounded-failure path.
    /// </summary>
    [TestClass]
    public sealed class TestReminders
    {
        private const ulong GuildId = 5;
        private const ulong AuthorId = 200;
        private const ulong OtherUserId = 201;
        private const ulong GuildOwnerId = 999;

        private static readonly DateTimeOffset BaseTimestamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        // --- Duration parsing (FR-62/FR-63) ---

        [TestMethod]
        [DataRow("10s", 10.0, 1)]
        [DataRow("5m", 300.0, 1)]
        [DataRow("3h", 10800.0, 1)]
        [DataRow("2d", 172800.0, 1)]
        [DataRow("1w", 604800.0, 1)]
        [DataRow("10seconds", 10.0, 1)]
        [DataRow("5 minutes", 300.0, 2)]
        [DataRow("3 hrs", 10800.0, 2)]
        [DataRow("2 days", 172800.0, 2)]
        [DataRow("1 week", 604800.0, 2)]
        [DataRow("10 d", 864000.0, 2)]
        [DataRow("5 h", 18000.0, 2)]
        [DataRow("1d 12h", 129600.0, 2)]
        [DataRow("10d 5 h", 882000.0, 3)]
        [DataRow("1d 12h water the plants", 129600.0, 2)]
        [DataRow("10D", 864000.0, 1)]
        public void DurationParsing_Accepts_All_Units_Long_Spaced_And_Compact_Forms(string arguments, double expectedSeconds, int expectedConsumedTokens)
        {
            bool parsed = ReminderDurationParser.TryParse(CommandArguments.Parse("!remindme " + arguments), out TimeSpan duration, out int consumedTokens);

            Assert.IsTrue(parsed, $"'{arguments}' should parse");
            Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), duration);
            Assert.AreEqual(expectedConsumedTokens, consumedTokens, "The message must start after the duration tokens");
        }

        [TestMethod]
        [DataRow("520w")]
        [DataRow("9999999d")]
        [DataRow("525600 minutes")]
        [DataRow("200000 h")]
        public void DurationParsing_Has_No_Maximum_Multi_Year_Durations_Accepted(string arguments)
        {
            // FR-63: no minimum or maximum — durations of years are accepted.
            bool parsed = ReminderDurationParser.TryParse(CommandArguments.Parse("!remindme " + arguments), out TimeSpan duration, out int _);

            Assert.IsTrue(parsed, $"'{arguments}' (a multi-year duration) should parse");
            Assert.IsTrue(duration >= TimeSpan.FromDays(365), "A year-plus duration must be accepted (525600 minutes is exactly one year)");
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("hello")]
        [DataRow("10")]
        [DataRow("10x")]
        [DataRow("d")]
        [DataRow("10 d5")]
        [DataRow("999999999999w")]
        [DataRow("0s hi")]
        [DataRow("0m hi")]
        [DataRow("0d 0h hi")]
        public void DurationParsing_Rejects_Unparsable_And_NonPositive_Input(string arguments)
        {
            bool parsed = ReminderDurationParser.TryParse(CommandArguments.Parse("!remindme " + arguments), out TimeSpan duration, out int consumedTokens);

            Assert.IsFalse(parsed, $"'{arguments}' should not parse");
            Assert.AreEqual(TimeSpan.Zero, duration);
            Assert.AreEqual(0, consumedTokens, "Nothing may be consumed from unparsable input");
        }

        // --- remindme: persistence, validation, confirmation (FR-61–FR-64, FR-70) ---

        private sealed class ReminderHarness
        {
            public DatabaseContext Database { get; } = DiscordTestDoubles.CreateDatabase();
            public Mock<IScheduledDeliverySignal> Signal { get; } = new();

            public async Task<List<string>> Execute(ICommand command, string content, ulong? guildId = GuildId, ulong authorId = AuthorId, DateTimeOffset? timestamp = null)
            {
                var sentMessages = new List<string>();
                var message = guildId is ulong guild
                    ? DiscordTestDoubles.CreateGuildMessage(content, authorId, GuildOwnerId, sentMessages, guild, timestamp)
                    : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages, timestamp);
                var context = DiscordTestDoubles.CreateContext(Database, guildId: guildId);

                await command.ExecuteCommand(message.Object, context);
                return sentMessages;
            }
        }

        private static ReminderHarness Harness() => new();

        [TestMethod]
        public async Task RemindMe_Persists_And_Confirms_Echoing_Message_And_Due_Time()
        {
            var harness = Harness();
            var command = new RemindMeCommand(harness.Signal.Object);

            List<string> replies = await harness.Execute(command, "!remindme 1d 12h water the plants", timestamp: BaseTimestamp);

            var reminder = harness.Database.Reminders.Single();
            Assert.AreEqual(AuthorId, reminder.OwnerDiscordId);
            Assert.AreEqual(0, reminder.Number);
            Assert.AreEqual("water the plants", reminder.Message);
            Assert.AreEqual(BaseTimestamp.Add(TimeSpan.FromHours(36)), reminder.DueAtUtc, "The due time must be the message timestamp plus the duration, in UTC");
            Assert.AreEqual(GuildId, reminder.GuildId);
            Assert.AreEqual(BaseTimestamp.UtcDateTime, reminder.CreatedAt);
            Assert.IsNull(reminder.FailedAtUtc, "A new reminder is pending");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "Reminder #0");
            StringAssert.Contains(replies[0], "water the plants");
            StringAssert.Contains(replies[0], BaseTimestamp.Add(TimeSpan.FromHours(36)).ToString("yyyy-MM-dd HH:mm"));

            // The new reminder may be the earliest pending one: the delivery signal must be notified (FR-65).
            harness.Signal.Verify(signal => signal.NotifyPendingSetChanged(), Times.Once);
        }

        [TestMethod]
        public async Task RemindMe_Works_In_Direct_Messages_With_Null_Guild()
        {
            var harness = Harness();
            var command = new RemindMeCommand(harness.Signal.Object);

            await harness.Execute(command, "!remindme 10m stretch", guildId: null, timestamp: BaseTimestamp);

            var reminder = harness.Database.Reminders.Single();
            Assert.IsNull(reminder.GuildId, "A reminder created in a direct message records no guild (FR-61)");
        }

        [TestMethod]
        public async Task RemindMe_Rejects_Empty_Over_Long_And_Unparsable_Input_Without_Storing()
        {
            var harness = Harness();
            var command = new RemindMeCommand(harness.Signal.Object);

            List<string> noMessage = await harness.Execute(command, "!remindme 10m");
            List<string> tooLong = await harness.Execute(command, "!remindme 10m " + new string('x', RemindMeCommand.MaxMessageLength + 1));
            List<string> unparsable = await harness.Execute(command, "!remindme bananas hello");
            List<string> nonPositive = await harness.Execute(command, "!remindme 0s hello");

            Assert.AreEqual(0, harness.Database.Reminders.Count(), "Nothing may be stored for rejected invocations");

            StringAssert.Contains(noMessage.Single(), "message");
            StringAssert.Contains(tooLong.Single(), "at most");
            StringAssert.Contains(unparsable.Single(), "Usage");
            StringAssert.Contains(nonPositive.Single(), "Usage");

            harness.Signal.Verify(scheduler => scheduler.NotifyPendingSetChanged(), Times.Never);
        }

        [TestMethod]
        public async Task RemindMe_Accepts_Message_At_Exactly_The_Length_Cap()
        {
            var harness = Harness();
            var command = new RemindMeCommand(harness.Signal.Object);

            List<string> replies = await harness.Execute(command, "!remindme 10m " + new string('x', RemindMeCommand.MaxMessageLength));

            Assert.AreEqual(1, harness.Database.Reminders.Count());
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "Reminder #0");
        }

        [TestMethod]
        public async Task RemindMe_Assigns_Sequential_Numbers_And_Cancel_Frees_The_Top_Number()
        {
            var harness = Harness();
            var remindMe = new RemindMeCommand(harness.Signal.Object);
            var cancel = new CancelReminderCommand(harness.Signal.Object);

            await harness.Execute(remindMe, "!remindme 10m one");
            await harness.Execute(remindMe, "!remindme 10m two");
            await harness.Execute(remindMe, "!remindme 10m three");

            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, harness.Database.Reminders.OrderBy(reminder => reminder.Number).Select(reminder => reminder.Number).ToList());

            // Cancelling the highest-numbered reminder frees its number for the next one;
            // the remaining ones keep their numbers.
            await harness.Execute(cancel, "!cancelreminder 2");
            await harness.Execute(remindMe, "!remindme 10m four");

            var numbers = harness.Database.Reminders.OrderBy(reminder => reminder.Number).Select(reminder => reminder.Number).ToList();
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, numbers);
            Assert.AreEqual("four", harness.Database.Reminders.Single(reminder => reminder.Number == 2).Message);
        }

        [TestMethod]
        public async Task RemindMe_Enforces_The_Per_User_Cap_Of_Ten_Pending()
        {
            var harness = Harness();
            var remindMe = new RemindMeCommand(harness.Signal.Object);
            var cancel = new CancelReminderCommand(harness.Signal.Object);

            for (int i = 0; i < RemindMeCommand.MaxPendingReminders; i++)
                await harness.Execute(remindMe, $"!remindme 10m reminder {i}");

            // The invoking user is at the cap: the eleventh is rejected and nothing is stored (FR-70).
            List<string> rejected = await harness.Execute(remindMe, "!remindme 10m one too many");
            Assert.AreEqual(RemindMeCommand.MaxPendingReminders, harness.Database.Reminders.Count());
            StringAssert.Contains(rejected.Single(), "10 pending reminders");

            // Another user is unaffected — the cap is per user.
            await harness.Execute(remindMe, "!remindme 10m someone else", authorId: OtherUserId);
            Assert.AreEqual(RemindMeCommand.MaxPendingReminders, harness.Database.Reminders.Count(reminder => reminder.OwnerDiscordId == AuthorId));
            Assert.AreEqual(1, harness.Database.Reminders.Count(reminder => reminder.OwnerDiscordId == OtherUserId));

            // Cancelling frees a slot.
            await harness.Execute(cancel, "!cancelreminder 0");
            List<string> accepted = await harness.Execute(remindMe, "!remindme 10m after cancel");
            StringAssert.Contains(accepted.Single(), "Reminder #");
            Assert.AreEqual(RemindMeCommand.MaxPendingReminders, harness.Database.Reminders.Count(reminder => reminder.OwnerDiscordId == AuthorId));
        }

        // --- reminders list (FR-68) ---

        [TestMethod]
        public async Task Reminders_Lists_Soonest_First_And_Excludes_Failed()
        {
            var harness = Harness();
            var now = BaseTimestamp;
            harness.Database.Reminders.AddRange(
                new DatabaseContext.Reminder { OwnerDiscordId = AuthorId, Number = 1, Message = "later", DueAtUtc = now.AddHours(2), GuildId = GuildId },
                new DatabaseContext.Reminder { OwnerDiscordId = AuthorId, Number = 0, Message = "sooner", DueAtUtc = now.AddHours(1), GuildId = GuildId },
                new DatabaseContext.Reminder { OwnerDiscordId = AuthorId, Number = 2, Message = "failed", DueAtUtc = now, GuildId = GuildId, FailedAtUtc = now },
                new DatabaseContext.Reminder { OwnerDiscordId = OtherUserId, Number = 0, Message = "foreign", DueAtUtc = now, GuildId = GuildId });
            await harness.Database.SaveChangesAsync();

            List<string> replies = await harness.Execute(new RemindersCommand(), "!reminders");

            string reply = replies.Single();
            StringAssert.Contains(reply, "#0");
            StringAssert.Contains(reply, "sooner");
            StringAssert.Contains(reply, "#1");
            StringAssert.Contains(reply, "later");
            Assert.IsFalse(reply.Contains("failed"), "Failed reminders are not pending");
            Assert.IsFalse(reply.Contains("foreign"), "Users never see other users' reminders (FR-69)");
            Assert.IsTrue(reply.IndexOf("sooner") < reply.IndexOf("later"), "Reminders are listed soonest first");
        }

        [TestMethod]
        public async Task Reminders_Empty_List_Gets_A_Friendly_Message()
        {
            var harness = Harness();

            List<string> replies = await harness.Execute(new RemindersCommand(), "!reminders");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "no pending reminders");
        }

        [TestMethod]
        public async Task Reminders_List_Is_Capped_At_Ten_Entries()
        {
            var harness = Harness();
            var now = BaseTimestamp;
            for (int number = 0; number < 15; number++)
                harness.Database.Reminders.Add(new DatabaseContext.Reminder { OwnerDiscordId = AuthorId, Number = number, Message = $"reminder {number}", DueAtUtc = now.AddMinutes(number), GuildId = GuildId });
            await harness.Database.SaveChangesAsync();

            List<string> replies = await harness.Execute(new RemindersCommand(), "!reminders");

            // Ten listed lines: the ten soonest; the later five are cut off (FR-68).
            Assert.AreEqual(10, replies[0].Split('\n').Length - 1);
            StringAssert.Contains(replies[0], "reminder 9");
            Assert.IsFalse(replies[0].Contains("reminder 10"), "The list is capped at 10 entries");
        }

        // --- cancelreminder (FR-69) ---

        [TestMethod]
        public async Task CancelReminder_Cancels_Confirms_And_Notifies_The_Scheduler()
        {
            var harness = Harness();
            var scheduler = new Mock<IScheduledDeliverySignal>();
            var cancel = new CancelReminderCommand(scheduler.Object);
            harness.Database.Reminders.Add(new DatabaseContext.Reminder { OwnerDiscordId = AuthorId, Number = 3, Message = "target", DueAtUtc = BaseTimestamp.AddHours(1), GuildId = GuildId });
            await harness.Database.SaveChangesAsync();

            List<string> replies = await harness.Execute(cancel, "!cancelreminder 3");

            Assert.AreEqual(0, harness.Database.Reminders.Count(), "The cancelled reminder must be removed");
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "Cancelled reminder #3");
            scheduler.Verify(s => s.NotifyPendingSetChanged(), Times.Once, "Cancelling may change the earliest due time (FR-65)");
        }

        [TestMethod]
        [DataRow("!cancelreminder")]
        [DataRow("!cancelreminder abc")]
        public async Task CancelReminder_Rejects_Malformed_Numbers_With_A_Usage_Hint(string content)
        {
            var harness = Harness();
            var cancel = new CancelReminderCommand(harness.Signal.Object);

            List<string> replies = await harness.Execute(cancel, content);

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "Please give the number");
        }

        [TestMethod]
        [DataRow("!cancelreminder 99")]
        [DataRow("!cancelreminder 3")]
        public async Task CancelReminder_Rejects_Unknown_And_Foreign_Numbers_Alike(string content)
        {
            var harness = Harness();
            var cancel = new CancelReminderCommand(harness.Signal.Object);
            // Reminder #3 belongs to another user: it must look exactly like an unknown number.
            harness.Database.Reminders.Add(new DatabaseContext.Reminder { OwnerDiscordId = OtherUserId, Number = 3, Message = "foreign", DueAtUtc = BaseTimestamp.AddHours(1), GuildId = GuildId });
            await harness.Database.SaveChangesAsync();

            List<string> replies = await harness.Execute(cancel, content);

            Assert.AreEqual(1, harness.Database.Reminders.Count(), "Nothing may be removed");
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "no pending reminder");
        }

        [TestMethod]
        public async Task CancelReminder_Cannot_Cancel_Failed_Reminders()
        {
            var harness = Harness();
            var cancel = new CancelReminderCommand(new Mock<IScheduledDeliverySignal>().Object);
            harness.Database.Reminders.Add(new DatabaseContext.Reminder { OwnerDiscordId = AuthorId, Number = 0, Message = "failed", DueAtUtc = BaseTimestamp, GuildId = GuildId, FailedAtUtc = BaseTimestamp });
            await harness.Database.SaveChangesAsync();

            List<string> replies = await harness.Execute(cancel, "!cancelreminder 0");

            Assert.AreEqual(1, harness.Database.Reminders.Count(), "A failed reminder stays for audit");
            StringAssert.Contains(replies[0], "no pending reminder");
        }

        [TestMethod]
        public void RegisterAllCommands_Registers_The_Reminder_Commands_At_Tier_Zero()
        {
            var commandService = new CommandService();
            commandService.RegisterCommands();

            string[] names = { "remindme", "reminders", "cancelreminder" };
            foreach (string name in names)
            {
                Assert.IsTrue(commandService.Commands.TryGetValue(name, out ICommand? command), $"!{name} must be registered");
                Assert.AreEqual(PermissionTier.Everyone, command.DefaultPermissionTier, "All reminder commands default to tier 0 (FR-71)");
            }
        }

        // --- Delivery (FR-65–FR-67) ---

        /// <summary>
        /// The delivery engine over a shared in-memory store: the real wake-up signal and reminder
        /// source, a fake clock, and a DM-sender double that records deliveries (and optionally
        /// simulates closed DMs for one owner).
        /// </summary>
        private sealed class DeliveryHarness : IDisposable
        {
            public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            public ScheduledDeliverySignal Signal { get; } = new();
            public Mock<IScheduledMessageSender<ScheduledReminderMessage>> DmSender { get; } = new();
            public List<(ulong Owner, string Message)> DeliveredMessages { get; } = new();

            /// <summary>When set, direct messages to this owner fail — simulating closed DMs (FR-67).</summary>
            public ulong? FailingOwnerId { get; set; }

            private readonly string _storeName = Guid.NewGuid().ToString();
            private readonly ServiceProvider _provider;
            private readonly CancellationTokenSource _stop = new();

            public ScheduledMessageDeliveryService<ScheduledReminderMessage> Service { get; }

            public DeliveryHarness()
            {
                DmSender
                    .Setup(sender => sender.SendAsync(It.IsAny<ScheduledReminderMessage>(), It.IsAny<CancellationToken>()))
                    .Returns((ScheduledReminderMessage message, CancellationToken _) =>
                    {
                        if (message.OwnerDiscordId == FailingOwnerId)
                            return Task.FromException(new InvalidOperationException("simulated closed DMs"));

                        DeliveredMessages.Add((message.OwnerDiscordId, message.Message));
                        return Task.CompletedTask;
                    });

                var services = new ServiceCollection();
                services.AddTransient<IDatabaseContext>(_ => CreateDatabase());
                _provider = services.BuildServiceProvider();

                var source = new ReminderMessageSource(
                    _provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<ReminderMessageSource>.Instance);

                Service = new ScheduledMessageDeliveryService<ScheduledReminderMessage>(
                    Signal,
                    source,
                    DmSender.Object,
                    Time,
                    NullLogger<ScheduledMessageDeliveryService<ScheduledReminderMessage>>.Instance);
            }

            public DatabaseContext CreateDatabase()
            {
                return new DatabaseContext(
                    new DbContextOptionsBuilder<DatabaseContext>()
                        .UseInMemoryDatabase(_storeName)
                        .Options);
            }

            public async Task StartAsync()
            {
                await Service.StartAsync(_stop.Token);
            }

            public async Task<DatabaseContext.Reminder> SeedReminderAsync(ulong owner, int number, string message, TimeSpan dueIn)
            {
                var database = CreateDatabase();
                var reminder = new DatabaseContext.Reminder
                {
                    OwnerDiscordId = owner,
                    Number = number,
                    Message = message,
                    DueAtUtc = Time.GetUtcNow() + dueIn,
                    GuildId = GuildId
                };
                database.Reminders.Add(reminder);
                await database.SaveChangesAsync();
                return reminder;
            }

            /// <summary>Runs a reminder command against the shared store with the harness clock's timestamp.</summary>
            public async Task<List<string>> Execute(ICommand command, string content, ulong authorId = AuthorId)
            {
                var sentMessages = new List<string>();
                var message = DiscordTestDoubles.CreateGuildMessage(content, authorId, GuildOwnerId, sentMessages, GuildId, Time.GetUtcNow());
                var context = DiscordTestDoubles.CreateContext(CreateDatabase(), guildId: GuildId);

                await command.ExecuteCommand(message.Object, context);
                return sentMessages;
            }

            public void Dispose()
            {
                _stop.Cancel();
                try
                {
                    Service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // Stopping is best-effort in tests; the loop ends with the cancelled token.
                }

                _stop.Dispose();
                _provider.Dispose();
            }
        }

        /// <summary>
        /// Polls until the condition holds. Between polls the fake clock is nudged forward one
        /// second so timer-chained waits (the fallback poll, the retry backoff) keep making
        /// progress even while the loop is between awaits on the thread pool.
        /// </summary>
        private static async Task WaitUntilAsync(Func<bool> condition, FakeTimeProvider time, int timeoutMilliseconds = 10_000)
        {
            for (int attempt = 0; attempt < timeoutMilliseconds / 25 && !condition(); attempt++)
            {
                await Task.Delay(25);
                time.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.IsTrue(condition(), "The expected condition was not met within the timeout");
        }

        private static bool ContainsMessage(IEnumerable<(ulong Owner, string Message)> delivered, string message)
        {
            return delivered.Any(entry => entry.Message == message);
        }

        [TestMethod]
        public async Task Delivery_Delivers_Due_Reminders_As_DM_And_Removes_Them()
        {
            using var harness = new DeliveryHarness();
            await harness.SeedReminderAsync(AuthorId, 0, "water the plants", TimeSpan.FromMinutes(5));
            await harness.SeedReminderAsync(AuthorId, 1, "call the doctor", TimeSpan.FromHours(1));
            await harness.StartAsync();

            // One second before the due time nothing is delivered (FR-65: second-accurate).
            harness.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, harness.DeliveredMessages.Count, "A reminder must not be delivered before its due time");

            harness.Time.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => ContainsMessage(harness.DeliveredMessages, "water the plants"), harness.Time);

            Assert.IsFalse(ContainsMessage(harness.DeliveredMessages, "call the doctor"), "A not-yet-due reminder must stay pending");
            Assert.AreEqual(1, harness.CreateDatabase().Reminders.Count());
            Assert.AreEqual("call the doctor", harness.CreateDatabase().Reminders.Single().Message, "Delivered reminders are removed from the pending set");
        }

        [TestMethod]
        public async Task Delivery_Reschedules_When_An_Earlier_Reminder_Becomes_Pending()
        {
            using var harness = new DeliveryHarness();
            var remindMe = new RemindMeCommand(harness.Signal);

            // The service starts with nothing pending; the first command's notification wakes it.
            await harness.StartAsync();
            await harness.Execute(remindMe, "!remindme 1h slow reminder");
            await harness.Execute(remindMe, "!remindme 5m quick reminder");

            harness.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, harness.DeliveredMessages.Count, "The earlier reminder must not be delivered before its due time");

            harness.Time.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => ContainsMessage(harness.DeliveredMessages, "quick reminder"), harness.Time);

            Assert.IsFalse(ContainsMessage(harness.DeliveredMessages, "slow reminder"), "Only the due reminder is delivered");
            Assert.AreEqual(1, harness.CreateDatabase().Reminders.Count(), "The later reminder stays pending");

            harness.Time.Advance(TimeSpan.FromMinutes(55));
            await WaitUntilAsync(() => ContainsMessage(harness.DeliveredMessages, "slow reminder"), harness.Time);
            Assert.AreEqual(0, harness.CreateDatabase().Reminders.Count(), "All reminders are delivered and removed");
        }

        [TestMethod]
        public async Task Delivery_Recovers_After_Restart()
        {
            using var harness = new DeliveryHarness();

            // A reminder that became due while the bot was down (FR-66): persisted and overdue.
            await harness.SeedReminderAsync(AuthorId, 0, "missed while offline", TimeSpan.FromMinutes(-30));
            await harness.StartAsync();

            await WaitUntilAsync(() => ContainsMessage(harness.DeliveredMessages, "missed while offline"), harness.Time);
            Assert.AreEqual(0, harness.CreateDatabase().Reminders.Count(), "The recovered reminder is delivered and removed");
        }

        [TestMethod]
        public async Task Delivery_Bounded_Failure_Marks_Failed_And_Never_Blocks_Other_Reminders()
        {
            using var harness = new DeliveryHarness();
            harness.FailingOwnerId = 300;

            // The failing reminder is due first, so it is attempted before the healthy one.
            await harness.SeedReminderAsync(300, 0, "into closed DMs", TimeSpan.FromMinutes(-10));
            await harness.SeedReminderAsync(AuthorId, 0, "healthy reminder", TimeSpan.FromMinutes(-5));
            await harness.StartAsync();

            await WaitUntilAsync(() =>
                ContainsMessage(harness.DeliveredMessages, "healthy reminder")
                && harness.CreateDatabase().Reminders.Any(reminder => reminder.OwnerDiscordId == 300 && reminder.FailedAtUtc != null),
                harness.Time);

            // Bounded retries (FR-67): exactly three attempts, then the row is marked failed.
            harness.DmSender.Verify(
                sender => sender.SendAsync(
                    It.Is<ScheduledReminderMessage>(message => message.OwnerDiscordId == 300 && message.Message == "into closed DMs"),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(ScheduledMessageDeliveryService<ScheduledReminderMessage>.MaxDeliveryAttempts));

            var database = harness.CreateDatabase();
            var failed = database.Reminders.Single(reminder => reminder.OwnerDiscordId == 300);
            Assert.IsNotNull(failed.FailedAtUtc, "The failed reminder is marked and logged, not deleted");
            Assert.AreEqual(0, database.Reminders.Count(reminder => reminder.OwnerDiscordId == AuthorId), "The healthy reminder was delivered and removed");
        }

        [TestMethod]
        public async Task Delivery_Keeps_Waiting_While_Nothing_Is_Pending()
        {
            using var harness = new DeliveryHarness();
            var remindMe = new RemindMeCommand(harness.Signal);

            // Nothing pending: the loop waits for the wake-up signal instead of polling (FR-65).
            await harness.StartAsync();
            Assert.AreEqual(0, harness.DeliveredMessages.Count);

            await harness.Execute(remindMe, "!remindme 1m wake up");
            harness.Time.Advance(TimeSpan.FromMinutes(1));
            await WaitUntilAsync(() => ContainsMessage(harness.DeliveredMessages, "wake up"), harness.Time);
            Assert.AreEqual(0, harness.CreateDatabase().Reminders.Count());
        }
    }
}
