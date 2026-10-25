using Discord;
using HambotV2.Backups;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using HambotV2.Maintenance;
using HambotV2.Watchdog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BotTests
{
    /// <summary>
    /// The watchdog, health, and notification features (FR-105–FR-106, FR-117–FR-118, NFR-23):
    /// threshold detection for each degradation condition, the alert-once/hourly-re-alert/recovery
    /// cadence, delivery via a recording sender to both the owner DM and the channel, the
    /// no-owner log fallback, the !health report's fields and tier gate, notification posting per
    /// event type with the rate limit and identical-message collapse, and !notifychannel's
    /// show/set/clear behavior with persistence.
    /// </summary>
    [TestClass]
    public sealed class TestWatchdog
    {
        private const ulong OwnerId = 250;
        private const ulong ChannelId = 777;
        private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // --- Fakes (NFR-23) ---

        private sealed class FakeGatewayStatus : IGatewayStatus
        {
            public bool Connected { get; set; } = true;
            public string State => Connected ? "Connected" : "Disconnected";
            public bool IsConnected => Connected;
        }

        private sealed class FakeBackupStatusReader : IBackupStatusReader
        {
            public BackupStatus? Status { get; set; }
            public bool ThrowOnRead { get; set; }

            public BackupStatus? Read()
            {
                if (ThrowOnRead)
                    throw new InvalidOperationException("simulated status-file failure");
                return Status;
            }
        }

        private sealed class FakePublisher : INotificationPublisher
        {
            public List<string> Published { get; } = new();
            public bool ChannelDeliveryFailed { get; set; }

            public Task PublishAsync(string message)
            {
                Published.Add(message);
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingSender : IOperatorNotificationSender
        {
            public List<(ulong Channel, string Message)> ChannelPosts { get; } = new();
            public List<(ulong User, string Message)> OwnerDms { get; } = new();
            public bool FailChannel { get; set; }
            public bool FailOwnerDm { get; set; }

            public Task SendToChannelAsync(ulong channelId, string message)
            {
                if (FailChannel)
                    return Task.FromException(new InvalidOperationException("simulated unreachable channel"));
                ChannelPosts.Add((channelId, message));
                return Task.CompletedTask;
            }

            public Task SendOwnerDmAsync(ulong userId, string message)
            {
                if (FailOwnerDm)
                    return Task.FromException(new InvalidOperationException("simulated closed DMs"));
                OwnerDms.Add((userId, message));
                return Task.CompletedTask;
            }
        }

        private sealed class WatchdogHarness
        {
            public FakeTimeProvider Time { get; } = new(StartTime);
            public FakeGatewayStatus Gateway { get; } = new();
            public MaintenanceMode Maintenance { get; } = new();
            public FakeBackupStatusReader Status { get; } = new();
            public FakePublisher Publisher { get; } = new();
            public RecordingSender Sender { get; } = new();

            private readonly IConfiguration _configuration;

            public WatchdogHarness(bool configureOwner = true)
            {
                var values = new Dictionary<string, string?>();
                if (configureOwner)
                    values["Permissions:BotOwnerDiscordId"] = OwnerId.ToString();
                _configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

                Service = new WatchdogService(
                    Gateway,
                    Maintenance,
                    Status,
                    Publisher,
                    Sender,
                    _configuration,
                    Time,
                    NullLogger<WatchdogService>.Instance);
            }

            public WatchdogService Service { get; }

            public IReadOnlyList<string> OwnerAlerts => Sender.OwnerDms.Select(entry => entry.Message).ToList();
        }

        // --- Watchdog: condition detection (FR-106) ---

        [TestMethod]
        public async Task Gateway_Down_Past_Threshold_Alerts_Realerts_Hourly_And_Announces_Recovery_Once()
        {
            var harness = new WatchdogHarness();
            harness.Gateway.Connected = false;

            // Below the 10-minute threshold: the outage is observed but not alerted.
            await harness.Service.CheckOnceAsync();
            Assert.AreEqual(0, harness.OwnerAlerts.Count, "A brief disconnect must not alert yet");

            harness.Time.Advance(TimeSpan.FromMinutes(10));
            await harness.Service.CheckOnceAsync();
            Assert.AreEqual(1, harness.OwnerAlerts.Count, "Past the threshold the condition alerts on its rising edge");
            StringAssert.Contains(harness.OwnerAlerts[0], "Watchdog alert: the Discord gateway has been disconnected");
            Assert.AreEqual(1, harness.Publisher.Published.Count, "The alert additionally reaches the notification channel (FR-118)");

            // While the condition persists: at most one re-alert per hour.
            harness.Time.Advance(TimeSpan.FromMinutes(30));
            await harness.Service.CheckOnceAsync();
            Assert.AreEqual(1, harness.OwnerAlerts.Count, "30 minutes after the alert there is no re-alert yet");

            harness.Time.Advance(TimeSpan.FromMinutes(31));
            await harness.Service.CheckOnceAsync();
            Assert.AreEqual(2, harness.OwnerAlerts.Count, "An hour after the last alert the condition re-alerts");
            StringAssert.Contains(harness.OwnerAlerts[1], "still active");

            // Recovery is announced exactly once.
            harness.Gateway.Connected = true;
            await harness.Service.CheckOnceAsync();
            Assert.AreEqual(3, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[2], "Watchdog recovery: the Discord gateway is connected again");

            await harness.Service.CheckOnceAsync();
            Assert.AreEqual(3, harness.OwnerAlerts.Count, "A recovered condition does not announce again");
        }

        [TestMethod]
        public async Task Backup_Validation_Failure_Alerts()
        {
            var harness = new WatchdogHarness();
            harness.Status.Status = new BackupStatus(
                LastSuccessfulBackup: new BackupStatusEntry("hambotv2-old.dump", StartTime - TimeSpan.FromHours(2), 2048),
                LastValidationError: new BackupValidationError(StartTime - TimeSpan.FromMinutes(5), "pg_restore --list failed"),
                LastRestoreTest: new RestoreTestResult(StartTime - TimeSpan.FromHours(20), true, "all tables present"),
                BackupIntervalSeconds: 86400,
                RestoreTestIntervalSeconds: 86400,
                UpdatedAt: StartTime);

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(1, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[0], "backup failed validation");
        }

        [TestMethod]
        public async Task Backup_Validation_Failure_Older_Than_The_Last_Success_Does_Not_Alert()
        {
            var harness = new WatchdogHarness();
            harness.Status.Status = new BackupStatus(
                LastSuccessfulBackup: new BackupStatusEntry("hambotv2-new.dump", StartTime - TimeSpan.FromMinutes(5), 2048),
                LastValidationError: new BackupValidationError(StartTime - TimeSpan.FromHours(2), "older failure, since recovered"),
                LastRestoreTest: new RestoreTestResult(StartTime - TimeSpan.FromHours(20), true, "all tables present"),
                BackupIntervalSeconds: 86400,
                RestoreTestIntervalSeconds: 86400,
                UpdatedAt: StartTime);

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(0, harness.OwnerAlerts.Count, "A validation failure superseded by a good backup is not a live condition");
        }

        [TestMethod]
        public async Task Backup_Overdue_Alerts()
        {
            var harness = new WatchdogHarness();
            harness.Status.Status = new BackupStatus(
                LastSuccessfulBackup: new BackupStatusEntry("hambotv2-old.dump", StartTime - TimeSpan.FromHours(3), 2048),
                LastValidationError: null,
                LastRestoreTest: new RestoreTestResult(StartTime - TimeSpan.FromHours(20), true, "all tables present"),
                BackupIntervalSeconds: 3600,
                RestoreTestIntervalSeconds: 86400,
                UpdatedAt: StartTime);

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(1, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[0], "no successful backup has completed within the scheduled interval");
        }

        [TestMethod]
        public async Task Backup_On_Schedule_Does_Not_Alert()
        {
            var harness = new WatchdogHarness();
            harness.Status.Status = new BackupStatus(
                LastSuccessfulBackup: new BackupStatusEntry("hambotv2-new.dump", StartTime - TimeSpan.FromMinutes(30), 2048),
                LastValidationError: null,
                LastRestoreTest: new RestoreTestResult(StartTime - TimeSpan.FromHours(20), true, "all tables present"),
                BackupIntervalSeconds: 3600,
                RestoreTestIntervalSeconds: 86400,
                UpdatedAt: StartTime);

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(0, harness.OwnerAlerts.Count, "A healthy backup schedule is not a degradation");
        }

        [TestMethod]
        public async Task Restore_Test_Failure_Alerts()
        {
            var harness = new WatchdogHarness();
            harness.Status.Status = new BackupStatus(
                LastSuccessfulBackup: new BackupStatusEntry("hambotv2-new.dump", StartTime - TimeSpan.FromMinutes(30), 2048),
                LastValidationError: null,
                LastRestoreTest: new RestoreTestResult(StartTime - TimeSpan.FromMinutes(10), false, "tables missing"),
                BackupIntervalSeconds: 3600,
                RestoreTestIntervalSeconds: 86400,
                UpdatedAt: StartTime);

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(1, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[0], "restore test failed");
        }

        [TestMethod]
        public async Task Restore_Test_Overdue_Alerts()
        {
            var harness = new WatchdogHarness();
            harness.Status.Status = new BackupStatus(
                LastSuccessfulBackup: new BackupStatusEntry("hambotv2-new.dump", StartTime - TimeSpan.FromMinutes(30), 2048),
                LastValidationError: null,
                LastRestoreTest: new RestoreTestResult(StartTime - TimeSpan.FromDays(2), true, "passed back then"),
                BackupIntervalSeconds: 3600,
                RestoreTestIntervalSeconds: 86400,
                UpdatedAt: StartTime);

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(1, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[0], "no automated restore test has run within the scheduled interval");
        }

        [TestMethod]
        public async Task Restore_Test_Never_Recorded_Does_Not_Alert_While_The_Status_Is_Fresh()
        {
            // A sidecar restart or fresh deployment briefly reports no restore test yet; the
            // watchdog must grant the interval-plus-grace window (from the last status
            // update) instead of alerting immediately on the gap.
            var harness = new WatchdogHarness();
            harness.Status.Status = new BackupStatus(
                LastSuccessfulBackup: new BackupStatusEntry("hambotv2-new.dump", StartTime - TimeSpan.FromMinutes(30), 2048),
                LastValidationError: null,
                LastRestoreTest: null,
                BackupIntervalSeconds: 3600,
                RestoreTestIntervalSeconds: 86400,
                UpdatedAt: StartTime - TimeSpan.FromMinutes(10));

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(0, harness.OwnerAlerts.Count, "A fresh status with no restore test recorded is not overdue yet");
        }

        [TestMethod]
        public async Task Restore_Test_Never_Recorded_Alerts_Once_The_Status_Goes_Stale_Past_The_Interval()
        {
            // No test recorded AND the sidecar has not updated its status for a full
            // interval plus grace: now it is genuinely overdue.
            var harness = new WatchdogHarness();
            harness.Status.Status = new BackupStatus(
                LastSuccessfulBackup: new BackupStatusEntry("hambotv2-new.dump", StartTime - TimeSpan.FromMinutes(30), 2048),
                LastValidationError: null,
                LastRestoreTest: null,
                BackupIntervalSeconds: 3600,
                RestoreTestIntervalSeconds: 3600,
                UpdatedAt: StartTime - TimeSpan.FromHours(4));

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(1, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[0], "no automated restore test has run within the scheduled interval");
        }

        [TestMethod]
        public async Task Maintenance_Mode_Alerts_And_Announces_Its_End()
        {
            var harness = new WatchdogHarness();
            harness.Maintenance.Enter();

            await harness.Service.CheckOnceAsync();
            Assert.AreEqual(1, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[0], "maintenance mode");

            harness.Maintenance.Exit();
            await harness.Service.CheckOnceAsync();
            Assert.AreEqual(2, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[1], "Watchdog recovery: maintenance mode has ended");
        }

        [TestMethod]
        public async Task Unreachable_Notification_Channel_Alerts()
        {
            var harness = new WatchdogHarness();
            harness.Publisher.ChannelDeliveryFailed = true;

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(1, harness.OwnerAlerts.Count);
            StringAssert.Contains(harness.OwnerAlerts[0], "notification channel could not be posted to");
        }

        [TestMethod]
        public async Task No_Configured_Bot_Owner_Degrades_To_Log_Only()
        {
            var harness = new WatchdogHarness(configureOwner: false);
            harness.Maintenance.Enter();

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(0, harness.Sender.OwnerDms.Count, "Without an owner there is nobody to DM (FR-106)");
            Assert.AreEqual(0, harness.Publisher.Published.Count, "Alerts degrade to logs only");
        }

        [TestMethod]
        public async Task Failing_Watchdog_Check_Never_Propagates()
        {
            var harness = new WatchdogHarness();
            harness.Status.ThrowOnRead = true;

            await harness.Service.CheckOnceAsync();

            Assert.AreEqual(0, harness.OwnerAlerts.Count, "A broken detector must not escalate into a false alert or a crash (FR-106)");
        }

        // --- NotificationPublisher (FR-118) ---

        private sealed class FakeChannelStore : INotificationChannelStore
        {
            public ulong? ChannelId { get; set; }

            public Task<ulong?> GetChannelIdAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(ChannelId);

            public Task SetChannelIdAsync(ulong? channelId, CancellationToken cancellationToken = default)
            {
                ChannelId = channelId;
                return Task.CompletedTask;
            }
        }

        private sealed class PublisherHarness
        {
            public FakeTimeProvider Time { get; } = new(StartTime);
            public FakeChannelStore Store { get; } = new();
            public RecordingSender Sender { get; } = new();

            public PublisherHarness(Dictionary<string, string?>? configurationValues = null)
            {
                var values = new Dictionary<string, string?>
                {
                    ["Permissions:BotOwnerDiscordId"] = OwnerId.ToString()
                };
                if (configurationValues is not null)
                    foreach (KeyValuePair<string, string?> pair in configurationValues)
                        values[pair.Key] = pair.Value;

                Publisher = new NotificationPublisher(
                    Store,
                    Sender,
                    new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                    Time,
                    NullLogger<NotificationPublisher>.Instance);
            }

            public NotificationPublisher Publisher { get; }
        }

        [TestMethod]
        public async Task Publish_Posts_To_The_Configured_Channel()
        {
            var harness = new PublisherHarness();
            harness.Store.ChannelId = ChannelId;

            await harness.Publisher.PublishAsync("Watchdog alert: something is wrong.");

            CollectionAssert.AreEqual(new[] { (ChannelId, "Watchdog alert: something is wrong.") }, harness.Sender.ChannelPosts);
            Assert.IsFalse(harness.Publisher.ChannelDeliveryFailed);
        }

        [TestMethod]
        public async Task Publish_Without_A_Channel_Logs_Only()
        {
            var harness = new PublisherHarness();

            await harness.Publisher.PublishAsync("a notification");

            Assert.AreEqual(0, harness.Sender.ChannelPosts.Count, "Without a channel, notifications degrade to logs and owner DMs (FR-118)");
        }

        [TestMethod]
        public async Task Publish_Rate_Limits_To_Five_Posts_Per_Minute()
        {
            var harness = new PublisherHarness();
            harness.Store.ChannelId = ChannelId;

            for (int i = 1; i <= 7; i++)
                await harness.Publisher.PublishAsync($"notification {i}");

            Assert.AreEqual(5, harness.Sender.ChannelPosts.Count, "The default cap is 5 posts per rolling minute (FR-118)");

            // The window slides: a minute later posts go through again.
            harness.Time.Advance(TimeSpan.FromSeconds(61));
            await harness.Publisher.PublishAsync("notification after the window");
            Assert.AreEqual(6, harness.Sender.ChannelPosts.Count);
        }

        [TestMethod]
        public async Task Publish_Collapses_Identical_Consecutive_Messages()
        {
            var harness = new PublisherHarness();
            harness.Store.ChannelId = ChannelId;

            await harness.Publisher.PublishAsync("Command execution failure: the same error");
            await harness.Publisher.PublishAsync("Command execution failure: the same error");
            await harness.Publisher.PublishAsync("Command execution failure: a different error");

            Assert.AreEqual(2, harness.Sender.ChannelPosts.Count, "An error storm of identical messages collapses instead of flooding (FR-118)");
        }

        [TestMethod]
        public async Task MaxPostsPerMinute_Is_Configurable()
        {
            var harness = new PublisherHarness(new Dictionary<string, string?>
            {
                ["Notifications:MaxPostsPerMinute"] = "2"
            });
            harness.Store.ChannelId = ChannelId;

            await harness.Publisher.PublishAsync("one");
            await harness.Publisher.PublishAsync("two");
            await harness.Publisher.PublishAsync("three");

            Assert.AreEqual(2, harness.Sender.ChannelPosts.Count, "The configured cap replaces the default");
        }

        [TestMethod]
        public async Task Failed_Channel_Post_DMs_The_Owner_And_Sets_The_Watchdog_Flag()
        {
            var harness = new PublisherHarness();
            harness.Store.ChannelId = ChannelId;
            harness.Sender.FailChannel = true;

            await harness.Publisher.PublishAsync("an important notification");

            Assert.IsTrue(harness.Publisher.ChannelDeliveryFailed, "The watchdog must be able to see the unreachable channel (FR-106)");
            Assert.AreEqual(1, harness.Sender.OwnerDms.Count, "The meta-failure additionally reaches the bot owner by DM (FR-118)");
            StringAssert.Contains(harness.Sender.OwnerDms[0].Message, "could not be posted");

            // A later successful post clears the flag.
            harness.Sender.FailChannel = false;
            await harness.Publisher.PublishAsync("a recovered notification");
            Assert.IsFalse(harness.Publisher.ChannelDeliveryFailed, "The condition clears when the channel is reachable again");
        }

        // --- !health (FR-105) ---

        private sealed class FakeHealthProvider : IHealthSnapshotProvider
        {
            public HealthSnapshot Snapshot { get; set; } = new()
            {
                Version = "1.0.0+abc1234",
                Uptime = TimeSpan.FromHours(2),
                GatewayState = "Connected",
                Database = "reachable",
                PendingReminders = "3",
                Backup = "2026-01-01 03:00 UTC — hambotv2-x.dump (2 KB)",
                RestoreTest = "2026-01-01 03:05 UTC — passed",
                NotificationChannel = "<#777> (777)"
            };

            public Task<HealthSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(Snapshot);
        }

        private static async Task<List<string>> ExecuteHealthCommandAsync(HealthCommand command, bool directMessage = false)
        {
            var sentMessages = new List<string>();
            var message = directMessage
                ? DiscordTestDoubles.CreateDirectMessage("!health", OwnerId, sentMessages)
                : DiscordTestDoubles.CreateGuildMessage("!health", OwnerId, 999, sentMessages, 5);
            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase(), guildId: directMessage ? null : 5);

            await command.ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        [TestMethod]
        public async Task Health_Command_Formats_The_Snapshot_Into_One_Report()
        {
            var command = new HealthCommand(new FakeHealthProvider());

            List<string> replies = await ExecuteHealthCommandAsync(command);

            Assert.AreEqual(1, replies.Count);
            string report = replies[0];
            StringAssert.Contains(report, "Version: 1.0.0+abc1234");
            StringAssert.Contains(report, "Gateway: Connected");
            StringAssert.Contains(report, "Database: reachable");
            StringAssert.Contains(report, "Last backup: 2026-01-01 03:00 UTC");
            StringAssert.Contains(report, "Last restore test: 2026-01-01 03:05 UTC — passed");
            StringAssert.Contains(report, "Pending reminders: 3");
            StringAssert.Contains(report, "Notification channel: <#777> (777)");
            StringAssert.Contains(report, "Uptime: 2h 0m");
        }

        [TestMethod]
        public async Task Health_Command_Works_In_Direct_Messages()
        {
            var command = new HealthCommand(new FakeHealthProvider());

            List<string> replies = await ExecuteHealthCommandAsync(command, directMessage: true);

            Assert.AreEqual(1, replies.Count, "!health reads no guild state and must work in DMs (FR-105)");
        }

        [TestMethod]
        public void Health_And_NotifyChannel_Commands_Default_To_Bot_Owner_Tier_With_Help_Text()
        {
            var health = new HealthCommand(new FakeHealthProvider());
            var notifyChannel = new NotifyChannelCommand();

            foreach (ICommand command in new ICommand[] { health, notifyChannel })
            {
                Assert.AreEqual(PermissionTier.BotOwner, command.DefaultPermissionTier, $"{command.Name} is a bot-owner command (FR-41)");
                Assert.IsFalse(string.IsNullOrWhiteSpace(command.Description));
                Assert.IsFalse(string.IsNullOrWhiteSpace(command.Usage));
                Assert.AreEqual(command.Name, command.Name.ToLower());
            }
        }

        [TestMethod]
        public void Health_And_NotifyChannel_Are_Registered()
        {
            var commandService = new CommandService();
            commandService.RegisterCommands();

            Assert.IsTrue(commandService.Commands.ContainsKey("health"), "!health must be registered");
            Assert.IsTrue(commandService.Commands.ContainsKey("notifychannel"), "!notifychannel must be registered");
        }

        // --- !notifychannel (FR-117) ---

        /// <summary>The real <see cref="NotificationChannelStore"/> over a shared in-memory database.</summary>
        private sealed class ChannelStoreHarness : IDisposable
        {
            private readonly ServiceProvider _provider;
            private readonly IConfiguration _configuration;

            public ChannelStoreHarness(Dictionary<string, string?>? configurationValues = null)
            {
                string storeName = Guid.NewGuid().ToString();
                var services = new ServiceCollection();
                services.AddTransient<IDatabaseContext>(_ => new DatabaseContext(
                    new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(storeName).Options));
                _provider = services.BuildServiceProvider();

                _configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
                    .Build();

                Store = new NotificationChannelStore(
                    _provider.GetRequiredService<IServiceScopeFactory>(),
                    _configuration);
            }

            public NotificationChannelStore Store { get; }

            /// <summary>A fresh store over the same database and configuration — the restart simulation.</summary>
            public NotificationChannelStore StoreAfterRestart()
                => new(_provider.GetRequiredService<IServiceScopeFactory>(), _configuration);

            public void Dispose() => _provider.Dispose();
        }

        private static async Task<List<string>> ExecuteNotifyChannelCommandAsync(NotifyChannelCommand command, string content, bool directMessage = false)
        {
            var sentMessages = new List<string>();
            var message = directMessage
                ? DiscordTestDoubles.CreateDirectMessage(content, OwnerId, sentMessages)
                : DiscordTestDoubles.CreateGuildMessage(content, OwnerId, 999, sentMessages, 5);
            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase(), guildId: directMessage ? null : 5);

            await command.ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        [TestMethod]
        public async Task NotifyChannel_Without_A_Channel_Reports_That_None_Is_Set()
        {
            using var harness = new ChannelStoreHarness();
            var command = new NotifyChannelCommand(harness.Store);

            List<string> replies = await ExecuteNotifyChannelCommandAsync(command, "!notifychannel");

            StringAssert.Contains(replies[0], "No notification channel is set");
        }

        [TestMethod]
        public async Task NotifyChannel_Sets_The_Channel_From_A_Mention_And_Persists_Across_Restarts()
        {
            using var harness = new ChannelStoreHarness();
            var command = new NotifyChannelCommand(harness.Store);

            List<string> replies = await ExecuteNotifyChannelCommandAsync(command, "!notifychannel <#1234>");
            StringAssert.Contains(replies[0], "<#1234>");

            Assert.AreEqual<ulong?>(1234, await harness.Store.GetChannelIdAsync(), "The mention must resolve to the channel id");

            // A fresh store over the same database: the setting survives a restart (FR-117).
            Assert.AreEqual<ulong?>(1234, await harness.StoreAfterRestart().GetChannelIdAsync(), "The setting must persist across restarts (FR-117)");
        }

        [TestMethod]
        public async Task NotifyChannel_Accepts_A_Raw_Channel_Id()
        {
            using var harness = new ChannelStoreHarness();
            var command = new NotifyChannelCommand(harness.Store);

            await ExecuteNotifyChannelCommandAsync(command, "!notifychannel 5678");

            Assert.AreEqual<ulong?>(5678, await harness.Store.GetChannelIdAsync());
        }

        [TestMethod]
        public async Task NotifyChannel_Rejects_Non_Channel_Input()
        {
            using var harness = new ChannelStoreHarness();
            var command = new NotifyChannelCommand(harness.Store);

            List<string> replies = await ExecuteNotifyChannelCommandAsync(command, "!notifychannel notachannel");

            StringAssert.Contains(replies[0], "does not look like a channel");
            Assert.IsNull(await harness.Store.GetChannelIdAsync(), "Nothing is stored on invalid input");
        }

        [TestMethod]
        public async Task NotifyChannel_Clear_Unsets_The_Runtime_Channel()
        {
            using var harness = new ChannelStoreHarness();
            var command = new NotifyChannelCommand(harness.Store);
            await ExecuteNotifyChannelCommandAsync(command, "!notifychannel 5678");

            List<string> replies = await ExecuteNotifyChannelCommandAsync(command, "!notifychannel clear");

            StringAssert.Contains(replies[0], "cleared");
            Assert.IsNull(await harness.Store.GetChannelIdAsync(), "Clear removes the runtime override");
        }

        [TestMethod]
        public async Task NotifyChannel_Clear_Falls_Back_To_The_Configured_Default()
        {
            using var harness = new ChannelStoreHarness(new Dictionary<string, string?>
            {
                ["Notifications:ChannelId"] = "999"
            });
            var command = new NotifyChannelCommand(harness.Store);

            // The runtime override shadows the configuration default...
            await ExecuteNotifyChannelCommandAsync(command, "!notifychannel 5678");
            Assert.AreEqual<ulong?>(5678, await harness.Store.GetChannelIdAsync());

            // ...and clearing it lets the default apply again (FR-117).
            await ExecuteNotifyChannelCommandAsync(command, "!notifychannel clear");
            Assert.AreEqual<ulong?>(999, await harness.Store.GetChannelIdAsync(), "The configuration default applies when no override exists");
        }

        [TestMethod]
        public async Task NotifyChannel_Works_In_Direct_Messages()
        {
            using var harness = new ChannelStoreHarness();
            var command = new NotifyChannelCommand(harness.Store);

            List<string> replies = await ExecuteNotifyChannelCommandAsync(command, "!notifychannel 1234", directMessage: true);

            Assert.AreEqual<ulong?>(1234, await harness.Store.GetChannelIdAsync(), "The setting is global and must be adjustable from DMs (FR-117)");
            Assert.AreEqual(1, replies.Count);
        }
    }
}
