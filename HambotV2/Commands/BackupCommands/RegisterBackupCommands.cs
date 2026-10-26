using HambotV2.Backups;
using HambotV2.Maintenance;
using HambotV2.Watchdog;

namespace HambotV2.Commands.BackupCommands
{
    /// <summary>Registers the backup-management commands in the registration chain (§3.2).</summary>
    internal static class RegisterBackupCommands
    {
        public static void RegisterCommands(
            ICommandService commandService,
            IBackupManager? backupManager = null,
            IMaintenanceMode? maintenanceMode = null,
            INotificationPublisher? notificationPublisher = null)
        {
            commandService.RegisterCommand("backup", new BackupCommand(backupManager));
            commandService.RegisterCommand("backups", new BackupsCommand(backupManager));
            commandService.RegisterCommand(RestoreBackupCommand.CommandName, new RestoreBackupCommand(backupManager, maintenanceMode, notificationPublisher));
        }
    }
}
