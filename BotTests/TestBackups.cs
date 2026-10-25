using Discord;
using HambotV2.Backups;
using HambotV2.Commands;
using HambotV2.Commands.BackupCommands;
using HambotV2.Connections;
using HambotV2.Maintenance;
using HambotV2.Scheduling;
using HambotV2.Reminders;
using HambotV2.Watchdog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace BotTests
{
    /// <summary>
    /// The backup-management commands (FR-96–FR-98, NFR-22): on-demand trigger and reply, list
    /// behavior and cap, the two-step restore confirmation, maintenance-mode gating of other
    /// commands and deferred reminder deliveries, the tier-4 gate, DM availability, and failure
    /// replies — all against a fake <see cref="IBackupManager"/> behind the process boundary.
    /// </summary>
    [TestClass]
    public sealed class TestBackups
    {
        private const ulong GuildId = 7;
        private const ulong AuthorId = 200;
        private const ulong BotOwnerId = 250;

        /// <summary>Backup manager double: canned list, counted calls, injectable failures (NFR-22).</summary>
        private sealed class FakeBackupManager : IBackupManager
        {
            public List<BackupInfo> Backups { get; } = new();
            public Exception? CreateFailure { get; set; }
            public Exception? RestoreFailure { get; set; }
            public int CreateCalls { get; private set; }
            public List<string> RestoreCalls { get; } = new();

            public Task<BackupInfo> CreateBackupAsync(CancellationToken cancellationToken = default)
            {
                CreateCalls++;
                return CreateFailure is not null
                    ? Task.FromException<BackupInfo>(CreateFailure)
                    : Task.FromResult(new BackupInfo($"hambotv2-test-{CreateCalls}.dump", DateTimeOffset.UtcNow, 2048));
            }

            public IReadOnlyList<BackupInfo> ListBackups() => Backups.OrderByDescending(backup => backup.Timestamp).ToList();

            public Task RestoreBackupAsync(string name, CancellationToken cancellationToken = default)
            {
                RestoreCalls.Add(name);
                return RestoreFailure is not null ? Task.FromException(RestoreFailure) : Task.CompletedTask;
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

        /// <summary>Runs a command against an isolated in-memory database, guild or DM.</summary>
        private static async Task<List<string>> ExecuteAsync(
            ICommand command,
            string content,
            Mock<IGuildSettingsService>? guildSettings = null,
            ulong? guildId = GuildId,
            ulong authorId = AuthorId)
        {
            var sentMessages = new List<string>();
            var message = guildId is ulong guild
                ? DiscordTestDoubles.CreateGuildMessage(content, authorId, 999, sentMessages, guild)
                : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);
            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase(), guildId: guildId, guildSettings: guildSettings?.Object);

            await command.ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        // --- The shared status file (§5.2/§3.13): the sidecar writes, the bot reads ---

        [TestMethod]
        public void BackupStatusFileReader_Reads_The_Sidecar_Status_File()
        {
            // The exact shape scripts/backup/run-backup.sh writes (jq camelCase JSON) — the
            // round-trip contract between the sidecar and !health/the watchdog (FR-100/FR-101).
            string directory = Path.Combine(Path.GetTempPath(), "hambotv2-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, ".backup-status"), """
                    {
                      "lastSuccessfulBackup": {
                        "name": "hambotv2-20261025-131837.dump",
                        "timestamp": "2026-10-25T13:18:38Z",
                        "sizeBytes": 6348
                      },
                      "lastValidationError": null,
                      "lastRestoreTest": {
                        "timestamp": "2026-10-25T13:17:37Z",
                        "succeeded": true,
                        "detail": "restore succeeded, all expected tables present"
                      },
                      "backupIntervalSeconds": 86400,
                      "restoreTestIntervalSeconds": 2592000,
                      "updatedAt": "2026-10-25T13:18:38Z"
                    }
                    """);

                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Backups:Directory"] = directory })
                    .Build();
                var reader = new BackupStatusFileReader(configuration, NullLogger<BackupStatusFileReader>.Instance);

                BackupStatus? status = reader.Read();

                Assert.IsNotNull(status, "A written status file must be readable");
                Assert.AreEqual("hambotv2-20261025-131837.dump", status.LastSuccessfulBackup?.Name);
                Assert.AreEqual(6348, status.LastSuccessfulBackup?.SizeBytes);
                Assert.IsNull(status.LastValidationError);
                Assert.IsTrue(status.LastRestoreTest?.Succeeded);
                Assert.AreEqual(86400, status.BackupIntervalSeconds);
                Assert.AreEqual(2592000, status.RestoreTestIntervalSeconds);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [TestMethod]
        public void BackupStatusFileReader_Missing_File_Returns_Null()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Backups:Directory"] = Path.Combine(Path.GetTempPath(), "hambotv2-test-" + Guid.NewGuid().ToString("N")) })
                .Build();
            var reader = new BackupStatusFileReader(configuration, NullLogger<BackupStatusFileReader>.Instance);

            Assert.IsNull(reader.Read(), "No sidecar deployment means no status — not an error");
        }

        // --- Registration contract (FR-96–FR-98) ---

        [TestMethod]
        public void Backup_Commands_Are_Registered_With_Help_Text_And_Bot_Owner_Tier()
        {
            var commandService = new CommandService();
            commandService.RegisterCommands();

            foreach (string name in new[] { "backup", "backups", "restorebackup" })
            {
                Assert.IsTrue(commandService.Commands.TryGetValue(name, out ICommand? command), $"!{name} must be registered");
                Assert.AreEqual(name, command.Name.ToLower(), "Command names must be lowercase");
                Assert.IsFalse(string.IsNullOrWhiteSpace(command.Description), $"!{name} needs help text (FR-39)");
                Assert.IsFalse(string.IsNullOrWhiteSpace(command.Usage), $"!{name} needs usage text (FR-39)");
                Assert.AreEqual(PermissionTier.BotOwner, command.DefaultPermissionTier, $"!{name} must default to bot owner tier (FR-41)");
            }
        }

        // --- !backup (FR-96) ---

        [TestMethod]
        public async Task Backup_Triggers_An_On_Demand_Backup_And_Replies_With_Name_And_Size()
        {
            var manager = new FakeBackupManager();
            var command = new BackupCommand(manager);

            List<string> replies = await ExecuteAsync(command, "!backup");

            Assert.AreEqual(1, manager.CreateCalls, "The command must trigger exactly one backup");
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "hambotv2-test-1.dump", "The reply must name the dump (FR-96)");
            StringAssert.Contains(replies[0], "2 KB", "The reply must state the dump's size (FR-96)");
        }

        [TestMethod]
        public async Task Backup_Failure_Produces_An_In_Channel_Error()
        {
            var manager = new FakeBackupManager { CreateFailure = new InvalidOperationException("pg_dump missing") };
            var command = new BackupCommand(manager);

            List<string> replies = await ExecuteAsync(command, "!backup");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "backup failed", "A failure must produce an in-channel error (FR-96)");
        }

        [TestMethod]
        public async Task Backup_Works_In_Direct_Messages()
        {
            var manager = new FakeBackupManager();
            var command = new BackupCommand(manager);

            List<string> replies = await ExecuteAsync(command, "!backup", guildId: null);

            Assert.AreEqual(1, manager.CreateCalls, "A global operation must work from DMs (FR-25)");
            Assert.AreEqual(1, replies.Count);
        }

        [TestMethod]
        public async Task Backup_Commands_Are_Gated_At_Tier_4()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var command = new BackupCommand(new FakeBackupManager());

            // The guild owner tops out at tier 3 — below the commands' tier-4 default.
            var guildOwnerOverrides = new TierOverrides { GuildOwnerId = 999 };
            Assert.IsFalse((await permissionsService.CheckUserPermissions(999, GuildId, guildOwnerOverrides, command, database)).IsAllowed,
                "A guild owner (tier 3) must not trigger backups");

            var botOwnerOverrides = new TierOverrides { BotOwnerId = BotOwnerId };
            Assert.IsTrue((await permissionsService.CheckUserPermissions(BotOwnerId, GuildId, botOwnerOverrides, command, database)).IsAllowed,
                "The configured bot owner must reach tier 4 (FR-41)");
        }

        // --- !backups (FR-97) ---

        [TestMethod]
        public async Task Backups_Lists_Newest_First_With_Name_Timestamp_And_Size()
        {
            var manager = new FakeBackupManager();
            var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            manager.Backups.AddRange(Enumerable.Range(0, 3).Select(i => new BackupInfo(
                $"hambotv2-{i}.dump", baseTime + TimeSpan.FromHours(i), 1024 * (i + 1))));
            var command = new BackupsCommand(manager);

            List<string> replies = await ExecuteAsync(command, "!backups");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "hambotv2-2.dump");
            int newestIndex = replies[0].IndexOf("hambotv2-2.dump", StringComparison.Ordinal);
            int oldestIndex = replies[0].IndexOf("hambotv2-0.dump", StringComparison.Ordinal);
            Assert.IsTrue(newestIndex < oldestIndex, "The list must be newest first (FR-97)");
        }

        [TestMethod]
        public async Task Backups_Empty_Directory_Gets_A_Friendly_Message()
        {
            var command = new BackupsCommand(new FakeBackupManager());

            List<string> replies = await ExecuteAsync(command, "!backups");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "no backups", "An empty directory is not an error (FR-97)");
        }

        [TestMethod]
        public async Task Backups_List_Is_Capped_At_Ten_Entries()
        {
            var manager = new FakeBackupManager();
            var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            manager.Backups.AddRange(Enumerable.Range(0, 12).Select(i => new BackupInfo(
                $"hambotv2-{i}.dump", baseTime + TimeSpan.FromMinutes(i), 2048)));
            var command = new BackupsCommand(manager);

            List<string> replies = await ExecuteAsync(command, "!backups");

            StringAssert.Contains(replies[0], "hambotv2-11.dump", "The newest entry must be listed");
            StringAssert.DoesNotMatch(replies[0], new System.Text.RegularExpressions.Regex("hambotv2-0\\.dump"));
            StringAssert.Contains(replies[0], "2 older backups", "The reply must name how many were cut (FR-97)");
        }

        // --- !restorebackup: the two-step confirmation (FR-98) ---

        [TestMethod]
        public async Task RestoreBackup_First_Invocation_Warns_And_Demands_Confirm()
        {
            var manager = new FakeBackupManager();
            manager.Backups.Add(new BackupInfo("hambotv2-x.dump", DateTimeOffset.UtcNow, 2048));
            var command = new RestoreBackupCommand(manager, new MaintenanceMode(), new FakePublisher());

            List<string> replies = await ExecuteAsync(command, "!restorebackup hambotv2-x.dump");

            Assert.AreEqual(0, manager.RestoreCalls.Count, "The first invocation must not restore (FR-98)");
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "REPLACE ALL CURRENT DATA", "The warning must name the consequences");
            StringAssert.Contains(replies[0], "!restorebackup hambotv2-x.dump confirm", "The warning must teach the confirm step");
        }

        [TestMethod]
        public async Task RestoreBackup_Unknown_Name_Is_Rejected()
        {
            var command = new RestoreBackupCommand(new FakeBackupManager(), new MaintenanceMode(), new FakePublisher());

            List<string> replies = await ExecuteAsync(command, "!restorebackup nope confirm");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "no backup named 'nope'");
        }

        [TestMethod]
        public async Task RestoreBackup_Second_Argument_Must_Be_Confirm()
        {
            var manager = new FakeBackupManager();
            manager.Backups.Add(new BackupInfo("hambotv2-x.dump", DateTimeOffset.UtcNow, 2048));
            var command = new RestoreBackupCommand(manager, new MaintenanceMode(), new FakePublisher());

            List<string> replies = await ExecuteAsync(command, "!restorebackup hambotv2-x.dump yes");

            Assert.AreEqual(0, manager.RestoreCalls.Count, "Anything but 'confirm' must not restore");
            StringAssert.Contains(replies[0], "confirm", "The reply must repeat the confirm step");
        }

        [TestMethod]
        public async Task RestoreBackup_Confirmed_Restores_And_Leaves_Maintenance_Mode()
        {
            var manager = new FakeBackupManager();
            manager.Backups.Add(new BackupInfo("hambotv2-x.dump", DateTimeOffset.UtcNow, 2048));
            var maintenance = new MaintenanceMode();
            var publisher = new FakePublisher();
            var guildSettings = new Mock<IGuildSettingsService>();
            var command = new RestoreBackupCommand(manager, maintenance, publisher);

            List<string> replies = await ExecuteAsync(command, "!restorebackup hambotv2-x.dump confirm", guildSettings);

            CollectionAssert.AreEqual(new[] { "hambotv2-x.dump" }, manager.RestoreCalls, "The confirmed invocation must restore the named backup");
            Assert.IsFalse(maintenance.IsActive, "Maintenance mode must end after the restore (FR-98)");
            StringAssert.Contains(replies[0], "restored successfully");
            guildSettings.Verify(settings => settings.InvalidateCache(), Times.Once,
                "Caches derived from the database must be invalidated after a restore (FR-98)");
            Assert.IsTrue(publisher.Published.Any(message => message.Contains("Maintenance mode")), "The restore must notify the operator channel (FR-118)");
        }

        [TestMethod]
        public async Task RestoreBackup_Failure_Reports_Generic_Error_And_Ends_Maintenance()
        {
            var manager = new FakeBackupManager { RestoreFailure = new InvalidOperationException("restore broke") };
            manager.Backups.Add(new BackupInfo("hambotv2-x.dump", DateTimeOffset.UtcNow, 2048));
            var maintenance = new MaintenanceMode();
            var command = new RestoreBackupCommand(manager, maintenance, new FakePublisher());

            List<string> replies = await ExecuteAsync(command, "!restorebackup hambotv2-x.dump confirm");

            Assert.IsFalse(maintenance.IsActive, "Maintenance mode must end even on failure (FR-98)");
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "restore failed", "A restore failure must produce an in-channel error");
            StringAssert.DoesNotMatch(replies[0], new System.Text.RegularExpressions.Regex("restore broke"),
                "Exception details must not be exposed in-channel (FR-15)");
        }

        // --- Maintenance-mode gating through the command pipeline (FR-98) ---

        /// <summary>A full pipeline over the real command registry with a controllable maintenance flag.</summary>
        private sealed class PipelineHarness
        {
            public MaintenanceMode Maintenance { get; } = new();
            public FakePublisher Publisher { get; } = new();

            private readonly CommandService _commandService = new();
            private readonly Mock<IGuildSettingsService> _guildSettings;
            private readonly DatabaseContext _database = DiscordTestDoubles.CreateDatabase();
            private readonly IConfiguration _configuration;

            public PipelineHarness()
            {
                _guildSettings = new Mock<IGuildSettingsService>();
                _guildSettings.SetupGet(settings => settings.GlobalCommandIdentifier).Returns("!");
                _guildSettings.Setup(settings => settings.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");

                var cooldown = new Mock<ICooldownService>();
                cooldown
                    .Setup(service => service.TryBeginInvocation(It.IsAny<ulong?>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDatabaseContext>()))
                    .Returns(CooldownCheck.Allowed);

                _configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Permissions:BotOwnerDiscordId"] = BotOwnerId.ToString()
                    })
                    .Build();

                Handler = new CommandHandler(
                    _commandService,
                    _guildSettings.Object,
                    new PermissionsService(),
                    cooldown.Object,
                    _database,
                    _configuration,
                    NullLogger<CommandHandler>.Instance,
                    Maintenance,
                    Publisher);
            }

            public CommandHandler Handler { get; }

            public async Task<List<string>> HandleAsync(string content, ulong authorId, bool directMessage = false)
            {
                var sentMessages = new List<string>();
                var message = directMessage
                    ? DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages)
                    : DiscordTestDoubles.CreateGuildMessage(content, authorId, 999, sentMessages, GuildId);
                await Handler.HandleCommand(message.Object);
                return sentMessages;
            }
        }

        [TestMethod]
        public async Task Maintenance_Mode_Declines_Other_Commands()
        {
            var harness = new PipelineHarness();
            harness.Maintenance.Enter();

            List<string> replies = await harness.HandleAsync("!hello", BotOwnerId);

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "maintenance mode", "Other commands must decline during a restore (FR-98)");
        }

        [TestMethod]
        public async Task Maintenance_Mode_Exempts_The_Restore_Command()
        {
            var harness = new PipelineHarness();
            harness.Maintenance.Enter();

            // The registry was built without a backup manager, so the restore command reports
            // "not configured" — but crucially it is not declined by the maintenance gate.
            List<string> replies = await harness.HandleAsync("!restorebackup x", BotOwnerId);

            Assert.AreEqual(1, replies.Count);
            Assert.IsFalse(replies[0].Contains("maintenance mode"), "The restore command must pass the maintenance gate (FR-98)");
        }

        [TestMethod]
        public async Task After_Maintenance_Mode_Commands_Run_Again()
        {
            var harness = new PipelineHarness();
            harness.Maintenance.Enter();
            await harness.HandleAsync("!hello", BotOwnerId);
            harness.Maintenance.Exit();

            List<string> replies = await harness.HandleAsync("!hello", BotOwnerId);

            CollectionAssert.AreEqual(new[] { "Hello World" }, replies, "The pipeline must be fully usable again after the restore");
        }

        // --- Deferred reminder deliveries during maintenance (FR-98) ---

        private sealed class DeliveryHarness : IDisposable
        {
            public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            public ScheduledDeliverySignal Signal { get; } = new();
            public MaintenanceMode Maintenance { get; } = new();
            public List<string> DeliveredMessages { get; } = new();

            private readonly string _storeName = Guid.NewGuid().ToString();
            private readonly ServiceProvider _provider;
            private readonly CancellationTokenSource _stop = new();

            public ScheduledMessageDeliveryService<ScheduledReminderMessage> Service { get; }

            public DeliveryHarness()
            {
                var sender = new Mock<IScheduledMessageSender<ScheduledReminderMessage>>();
                sender
                    .Setup(instance => instance.SendAsync(It.IsAny<ScheduledReminderMessage>(), It.IsAny<CancellationToken>()))
                    .Callback<ScheduledReminderMessage, CancellationToken>((message, _) => DeliveredMessages.Add(message.Message))
                    .Returns(Task.CompletedTask);

                var services = new ServiceCollection();
                services.AddTransient<IDatabaseContext>(_ => new DatabaseContext(
                    new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(_storeName).Options));
                _provider = services.BuildServiceProvider();

                var source = new ReminderMessageSource(
                    _provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<ReminderMessageSource>.Instance);

                Service = new ScheduledMessageDeliveryService<ScheduledReminderMessage>(
                    Signal,
                    source,
                    sender.Object,
                    Time,
                    NullLogger<ScheduledMessageDeliveryService<ScheduledReminderMessage>>.Instance,
                    Maintenance);
            }

            public async Task StartAsync() => await Service.StartAsync(_stop.Token);

            public async Task SeedDueReminderAsync(string text)
            {
                using var scope = _provider.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
                database.Reminders.Add(new DatabaseContext.Reminder
                {
                    OwnerDiscordId = AuthorId,
                    Number = 0,
                    Message = text,
                    DueAtUtc = Time.GetUtcNow(),
                    GuildId = GuildId
                });
                await database.SaveChangesAsync();
                Signal.NotifyPendingSetChanged();
            }

            public async Task<bool> ReminderIsPendingAsync()
            {
                using var scope = _provider.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
                var stored = await database.Reminders.FindAsync(new object[] { AuthorId, 0 });
                return stored is { FailedAtUtc: null };
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

                _provider.Dispose();
                _stop.Dispose();
            }
        }

        /// <summary>
        /// Polls until the condition holds. Between polls the fake clock is nudged forward one
        /// second so timer-chained waits (the fallback poll, the maintenance re-check) keep making
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

        [TestMethod]
        public async Task Maintenance_Mode_Defers_Reminder_Deliveries_Without_Marking_Them_Failed()
        {
            using var harness = new DeliveryHarness();
            await harness.SeedDueReminderAsync("water the plants");
            harness.Maintenance.Enter();

            await harness.StartAsync();

            // Let several delivery passes' worth of time pass while maintenance is active.
            for (int attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(25);
                harness.Time.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.AreEqual(0, harness.DeliveredMessages.Count, "Deliveries must defer during a restore (FR-98)");
            Assert.IsTrue(await harness.ReminderIsPendingAsync(), "No reminder may be marked failed due to a running restore (FR-98)");

            harness.Maintenance.Exit();
            harness.Signal.NotifyPendingSetChanged();
            await WaitUntilAsync(() => harness.DeliveredMessages.Count == 1, harness.Time);

            Assert.AreEqual("water the plants", harness.DeliveredMessages[0], "The deferred reminder is delivered once maintenance ends");
            Assert.IsFalse(await harness.ReminderIsPendingAsync(), "The delivered reminder leaves the pending set");
        }

        [TestMethod]
        public void Connection_Uri_Converts_The_Npgsql_Connection_String_For_Pg_Tools()
        {
            // pg_dump/pg_restore speak libpq, not Npgsql keyword syntax — the manager must
            // translate ("Server=…;User Id=…" → postgresql://user:pass@host:port/database).
            string uri = ProcessBackupManager.BuildPostgresUri(
                "Server=db;Port=5432;Database=hambotdb;User Id=hambot;Password=postgrepass");

            Assert.AreEqual("postgresql://hambot:postgrepass@db:5432/hambotdb", uri);
        }

        [TestMethod]
        public void Connection_Uri_Percent_Encodes_Special_Characters()
        {
            string uri = ProcessBackupManager.BuildPostgresUri(
                "Server=db;Port=5432;Database=hambotdb;User Id=ham bot;Password=p@ss:w/rd?x");

            Assert.AreEqual("postgresql://ham%20bot:p%40ss%3Aw%2Frd%3Fx@db:5432/hambotdb", uri);
        }

        [TestMethod]
        public void Connection_Uri_Omits_Credentials_When_The_String_Has_None()
        {
            string uri = ProcessBackupManager.BuildPostgresUri("Server=localhost;Port=5432;Database=hambotdb");

            Assert.AreEqual("postgresql://localhost:5432/hambotdb", uri);
        }
    }
}
