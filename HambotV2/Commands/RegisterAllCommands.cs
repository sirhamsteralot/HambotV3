using HambotV2.Backups;
using HambotV2.Commands.BackupCommands;
using HambotV2.Commands.QuoteCommands;
using HambotV2.Commands.ReminderCommands;
using HambotV2.Commands.ResponseCommands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Maintenance;
using HambotV2.Scheduling;
using HambotV2.Watchdog;

namespace HambotV2.Commands
{
    /// <summary>
    /// The head of the command registration chain; new command groups plug in here. The
    /// backup & stability dependencies (backup manager, maintenance mode, notification channel store,
    /// notification publisher, health snapshot provider) flow through from DI; they stay optional
    /// so the registry — and with it the registration contract tests — can be built without them
    /// (the affected commands then report "not configured" at execution time, which never happens
    /// in a DI-composed host).
    /// </summary>
    internal static class RegisterAllCommands
    {
        public static void RegisterCommands(
            ICommandService commandService,
            IScheduledDeliverySignal deliverySignal,
            IBackupManager? backupManager = null,
            IMaintenanceMode? maintenanceMode = null,
            INotificationPublisher? notificationPublisher = null,
            INotificationChannelStore? notificationChannelStore = null,
            IHealthSnapshotProvider? healthSnapshotProvider = null)
        {
            RegisterSystemCommands.RegisterCommands(commandService, notificationChannelStore, healthSnapshotProvider);
            RegisterQuoteCommands.RegisterCommands(commandService);
            RegisterResponseCommands.RegisterCommands(commandService);
            RegisterReminderCommands.RegisterCommands(commandService, deliverySignal);
            RegisterBackupCommands.RegisterCommands(commandService, backupManager, maintenanceMode, notificationPublisher);
        }
    }
}
