using HambotV2.Backups;
using HambotV2.Maintenance;

namespace HambotV2.Watchdog
{
    /// <summary>
    /// The watchdog loop (FR-106): every check interval it evaluates operator-relevant degradation —
    /// gateway down past a threshold, backup trouble read from the shared status file (failed
    /// validation, overdue backups, failed/overdue restore test), maintenance mode, and the
    /// notification channel being unreachable — and alerts the bot owner by DM, additionally
    /// posting to the configured notification channel. Each condition alerts once on its rising
    /// edge, re-alerts at most once per re-alert interval while it persists, and announces recovery
    /// once. Without a configured bot owner the alerts degrade to logs. The LLM conditions
    /// (§3.12) are dormant until that phase lands. A failing check is logged, never propagated.
    /// </summary>
    internal sealed class WatchdogService : BackgroundService
    {
        internal const int DefaultCheckIntervalMinutes = 5;
        internal const int DefaultGatewayDownAlertMinutes = 10;
        internal const int DefaultReAlertMinutes = 60;
        internal const int DefaultOverdueGraceMinutes = 60;

        private readonly IGatewayStatus _gateway;
        private readonly IMaintenanceMode _maintenanceMode;
        private readonly IBackupStatusReader _backupStatus;
        private readonly INotificationPublisher _publisher;
        private readonly IOperatorNotificationSender _sender;
        private readonly IConfiguration _configuration;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<WatchdogService> _logger;

        private readonly Dictionary<string, ConditionState> _conditions = new();

        public WatchdogService(
            IGatewayStatus gateway,
            IMaintenanceMode maintenanceMode,
            IBackupStatusReader backupStatus,
            INotificationPublisher publisher,
            IOperatorNotificationSender sender,
            IConfiguration configuration,
            TimeProvider timeProvider,
            ILogger<WatchdogService> logger)
        {
            _gateway = gateway;
            _maintenanceMode = maintenanceMode;
            _backupStatus = backupStatus;
            _publisher = publisher;
            _sender = sender;
            _configuration = configuration;
            _timeProvider = timeProvider;
            _logger = logger;

            RegisterCondition("gateway-down",
                "the Discord gateway has been disconnected",
                "the Discord gateway is connected again",
                () => Task.FromResult(IsGatewayDownPastThreshold()));
            RegisterCondition("backup-validation-failed",
                "the most recent backup failed validation",
                "backups are validating again",
                () => Task.FromResult(IsBackupValidationFailed()));
            RegisterCondition("backup-overdue",
                "no successful backup has completed within the scheduled interval",
                "backups are back on schedule",
                () => Task.FromResult(IsBackupOverdue()));
            RegisterCondition("restore-test-failed",
                "the most recent automated restore test failed",
                "the automated restore test passes again",
                () => Task.FromResult(IsRestoreTestFailed()));
            RegisterCondition("restore-test-overdue",
                "no automated restore test has run within the scheduled interval",
                "the automated restore test is back on schedule",
                () => Task.FromResult(IsRestoreTestOverdue()));
            RegisterCondition("maintenance-mode",
                "the bot is in maintenance mode",
                "maintenance mode has ended",
                () => Task.FromResult(_maintenanceMode.IsActive));
            RegisterCondition("notification-channel-unreachable",
                "the notification channel could not be posted to",
                "the notification channel is reachable again",
                () => Task.FromResult(_publisher.ChannelDeliveryFailed));
        }

        private DateTimeOffset? _gatewayDownSince;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Watchdog service started (checking every {Interval} minutes).", CheckIntervalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failing watchdog check must never crash the bot (FR-106).
                    _logger.LogError(ex, "Watchdog check failed.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(CheckIntervalMinutes), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Watchdog service stopped.");
        }

        /// <summary>One pass over all conditions; exposed so tests drive the cadence with a fake time provider (NFR-23).</summary>
        public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();

            foreach ((string key, ConditionState state) in _conditions)
            {
                bool active;
                try
                {
                    active = await state.Detect();
                }
                catch (Exception ex)
                {
                    // A broken detector never escalates into a false alert or a crash.
                    _logger.LogError(ex, "Watchdog condition '{ConditionKey}' failed to evaluate.", key);
                    continue;
                }

                if (active && !state.Active)
                {
                    state.Active = true;
                    await AlertAsync(state, $"Watchdog alert: {state.AlertDescription}.");
                }
                else if (active && now - state.LastAlertUtc >= ReAlertInterval)
                {
                    await AlertAsync(state, $"Watchdog alert (still active): {state.AlertDescription}.");
                }
                else if (!active && state.Active)
                {
                    state.Active = false;
                    await AlertAsync(state, $"Watchdog recovery: {state.RecoveryDescription}.");
                }
            }
        }

        private async Task AlertAsync(ConditionState state, string message)
        {
            state.LastAlertUtc = _timeProvider.GetUtcNow();

            // Alerts always reach the log — the owner/channel delivery is best-effort.
            _logger.LogWarning("Watchdog condition '{ConditionKey}': {Message}", state.Key, message);

            if (!ulong.TryParse(_configuration["Permissions:BotOwnerDiscordId"], out ulong ownerId))
            {
                // Without a configured bot owner the alerts degrade to logs only (FR-106).
                return;
            }

            try
            {
                await _sender.SendOwnerDmAsync(ownerId, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to DM the bot owner about watchdog condition '{ConditionKey}'.", state.Key);
            }

            try
            {
                // Additionally posted to the configured channel when one is set (FR-118);
                // the publisher's rate limit and collapse apply, and it never throws.
                await _publisher.PublishAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish the watchdog alert for '{ConditionKey}' to the notification channel.", state.Key);
            }
        }

        private void RegisterCondition(string key, string alertDescription, string recoveryDescription, Func<Task<bool>> detect)
        {
            _conditions[key] = new ConditionState(key, alertDescription, recoveryDescription, detect);
        }

        // --- Condition detection (FR-106) ---

        private bool IsGatewayDownPastThreshold()
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_gateway.IsConnected)
            {
                _gatewayDownSince = null;
                return false;
            }

            // The threshold is measured from the first observation of the outage, so brief
            // Discord.Net reconnect cycles do not alert.
            _gatewayDownSince ??= now;
            return now - _gatewayDownSince >= GatewayDownAlertInterval;
        }

        private bool IsBackupValidationFailed()
        {
            BackupStatus? status = _backupStatus.Read();
            return status?.LastValidationError is { } validation
                && (status.LastSuccessfulBackup is not { } success || validation.Timestamp > success.Timestamp);
        }

        private bool IsBackupOverdue()
        {
            BackupStatus? status = _backupStatus.Read();
            if (status is null)
                return false; // no backup sidecar deployed: not a degradation of the bot itself

            // The sidecar reports its own schedule; the grace margin absorbs one slow pass.
            if (status.LastSuccessfulBackup is not { } success)
                return true;

            int intervalSeconds = status.BackupIntervalSeconds ?? 0;
            if (intervalSeconds <= 0)
                return false;

            return _timeProvider.GetUtcNow() > success.Timestamp + TimeSpan.FromSeconds(intervalSeconds) + OverdueGraceInterval;
        }

        private bool IsRestoreTestFailed()
        {
            BackupStatus? status = _backupStatus.Read();
            return status?.LastRestoreTest is { Succeeded: false };
        }

        private bool IsRestoreTestOverdue()
        {
            BackupStatus? status = _backupStatus.Read();
            if (status is null)
                return false;

            int intervalSeconds = status.RestoreTestIntervalSeconds ?? 0;
            if (intervalSeconds <= 0)
                return false;

            if (status.LastRestoreTest is not { } test)
            {
                // No test recorded yet. The sidecar seeds its status file across restarts and runs
                // a test on its first pass, so this normally lasts seconds on a fresh volume; grant
                // the same interval-plus-grace window measured from the sidecar's own last status
                // update, so a sidecar restart or fresh deployment cannot trip a false alert.
                return _timeProvider.GetUtcNow() > status.UpdatedAt + TimeSpan.FromSeconds(intervalSeconds) + OverdueGraceInterval;
            }

            return _timeProvider.GetUtcNow() > test.Timestamp + TimeSpan.FromSeconds(intervalSeconds) + OverdueGraceInterval;
        }

        // --- Configuration (defaults in code; overridable via Watchdog:* keys) ---

        private int CheckIntervalMinutes => ReadPositiveMinutes("Watchdog:CheckIntervalMinutes", DefaultCheckIntervalMinutes);
        private TimeSpan GatewayDownAlertInterval => TimeSpan.FromMinutes(ReadPositiveMinutes("Watchdog:GatewayDownAlertMinutes", DefaultGatewayDownAlertMinutes));
        private TimeSpan ReAlertInterval => TimeSpan.FromMinutes(ReadPositiveMinutes("Watchdog:ReAlertMinutes", DefaultReAlertMinutes));
        private TimeSpan OverdueGraceInterval => TimeSpan.FromMinutes(ReadPositiveMinutes("Watchdog:OverdueGraceMinutes", DefaultOverdueGraceMinutes));

        private int ReadPositiveMinutes(string key, int fallback)
        {
            return int.TryParse(_configuration[key], out int minutes) && minutes > 0 ? minutes : fallback;
        }

        private sealed class ConditionState
        {
            public ConditionState(string key, string alertDescription, string recoveryDescription, Func<Task<bool>> detect)
            {
                Key = key;
                AlertDescription = alertDescription;
                RecoveryDescription = recoveryDescription;
                Detect = detect;
            }

            public string Key { get; }

            public string AlertDescription { get; }

            public string RecoveryDescription { get; }

            public Func<Task<bool>> Detect { get; }

            /// <summary>True while the condition is currently detected as active.</summary>
            public bool Active { get; set; }

            /// <summary>When the condition last alerted (drives the hourly re-alert cadence).</summary>
            public DateTimeOffset LastAlertUtc { get; set; }
        }
    }
}
