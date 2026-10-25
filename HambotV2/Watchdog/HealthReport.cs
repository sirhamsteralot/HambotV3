using HambotV2.Backups;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HambotV2.Watchdog
{
    /// <summary>
    /// One capture of the bot's operational state (FR-105): everything the `!health` report shows.
    /// The string fields are pre-formatted so the command stays a pure formatter, and unit tests
    /// assert the fields against a fake provider (NFR-23).
    /// </summary>
    internal sealed record HealthSnapshot
    {
        public required string Version { get; init; }
        public required TimeSpan Uptime { get; init; }
        public required string GatewayState { get; init; }
        public required string Database { get; init; }
        public required string PendingReminders { get; init; }
        public required string Backup { get; init; }
        public required string RestoreTest { get; init; }
        public required string NotificationChannel { get; init; }
    }

    /// <summary>
    /// Aggregates the health snapshot from the existing components (§3.13) — nothing is re-probed
    /// ad hoc by the command. Two consumers share it: `!health` (FR-105) and (indirectly, via the
    /// same status file) the watchdog (FR-106).
    /// </summary>
    internal interface IHealthSnapshotProvider
    {
        Task<HealthSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
    }

    internal sealed class HealthReport : IHealthSnapshotProvider
    {
        private readonly IGatewayStatus _gateway;
        private readonly INotificationChannelStore _notificationChannel;
        private readonly IBackupStatusReader _backupStatus;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly string _connectionString;
        private readonly TimeProvider _timeProvider;
        private readonly DateTimeOffset _startedAtUtc;

        public HealthReport(
            IGatewayStatus gateway,
            INotificationChannelStore notificationChannel,
            IBackupStatusReader backupStatus,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            TimeProvider timeProvider)
        {
            _gateway = gateway;
            _notificationChannel = notificationChannel;
            _backupStatus = backupStatus;
            _scopeFactory = scopeFactory;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _timeProvider = timeProvider;
            _startedAtUtc = timeProvider.GetUtcNow();
        }

        public async Task<HealthSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
        {
            // The LLM (§3.12), MCP (§3.11), and presence (§3.10) fields are omitted until
            // those phases land — the report shows only what is actually running today.
            return new HealthSnapshot
            {
                Version = BotVersion.Current,
                Uptime = _timeProvider.GetUtcNow() - _startedAtUtc,
                GatewayState = _gateway.State,
                Database = await ProbeDatabaseAsync(cancellationToken),
                PendingReminders = await CountPendingRemindersAsync(cancellationToken),
                Backup = FormatLastBackup(),
                RestoreTest = FormatRestoreTest(),
                NotificationChannel = FormatNotificationChannel(await _notificationChannel.GetChannelIdAsync(cancellationToken))
            };
        }

        /// <summary>A cheap reachability probe; details of a failure go to the log, not the report.</summary>
        private async Task<string> ProbeDatabaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                return "reachable";
            }
            catch (Exception)
            {
                // The report names the state, not the exception (FR-15 spirit); the watchdog
                // covers database trouble through its other conditions.
                return "unreachable";
            }
        }

        private async Task<string> CountPendingRemindersAsync(CancellationToken cancellationToken)
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
                int pending = await database.Reminders.CountAsync(reminder => reminder.FailedAtUtc == null, cancellationToken);
                return pending.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private string FormatLastBackup()
        {
            BackupStatus? status = _backupStatus.Read();
            if (status?.LastSuccessfulBackup is not { } backup)
                return "no backup status available (no backup service running?)";

            return $"{backup.Timestamp:yyyy-MM-dd HH:mm} UTC — {backup.Name} ({BackupSize.Format(backup.SizeBytes)})";
        }

        private string FormatRestoreTest()
        {
            BackupStatus? status = _backupStatus.Read();
            if (status?.LastRestoreTest is not { } test)
                return status is null ? "no backup status available" : "never run";

            return test.Succeeded
                ? $"{test.Timestamp:yyyy-MM-dd HH:mm} UTC — passed"
                : $"{test.Timestamp:yyyy-MM-dd HH:mm} UTC — FAILED: {test.Detail}";
        }

        private static string FormatNotificationChannel(ulong? channelId)
        {
            return channelId is ulong channel ? $"<#{channel}> ({channel})" : "not set (notifications go to logs and bot-owner DMs)";
        }
    }
}
