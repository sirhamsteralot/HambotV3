using Discord;
using HambotV2.Backups;
using HambotV2.Maintenance;
using HambotV2.Watchdog;
using Npgsql;

namespace HambotV2.Commands.BackupCommands
{
    /// <summary>
    /// Restores a listed backup into the live database as a two-step, explicitly confirmed
    /// operation: `!restorebackup &lt;name&gt;` warns that all current data will be replaced and demands the
    /// `confirm` suffix; only `!restorebackup &lt;name&gt; confirm` executes. While the restore runs the
    /// bot is in maintenance mode (other commands decline, scheduled deliveries defer), pooled
    /// database connections are cleared, and afterwards the guild-settings cache is invalidated.
    /// Bot-owner tier by default, usable in direct messages, with help text. The scripted procedure
    /// (scripts/restore-backup.sh) remains the authoritative recovery path when the bot itself is
    /// unavailable.
    /// </summary>
    internal sealed class RestoreBackupCommand : ICommand
    {
        internal const string CommandName = "restorebackup";

        private readonly IBackupManager? _backupManager;
        private readonly IMaintenanceMode? _maintenanceMode;
        private readonly INotificationPublisher? _notificationPublisher;

        public RestoreBackupCommand(IBackupManager? backupManager = null, IMaintenanceMode? maintenanceMode = null, INotificationPublisher? notificationPublisher = null)
        {
            _backupManager = backupManager;
            _maintenanceMode = maintenanceMode;
            _notificationPublisher = notificationPublisher;
        }

        public string Name => CommandName;
        public string Description => "Restores a database backup (destructive — requires an explicit confirm step)";
        public string Usage => "restorebackup <name> [confirm]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_backupManager is null || _maintenanceMode is null)
            {
                await message.Channel.SendMessageAsync("Backups are not configured on this installation.");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            string? name = arguments.Get(0);
            if (string.IsNullOrWhiteSpace(name))
            {
                await message.Channel.SendMessageAsync("Name the backup to restore — usage: !restorebackup <name> (run !backups to list them)");
                return;
            }

            if (_backupManager.ListBackups().All(backup => backup.Name != name))
            {
                await message.Channel.SendMessageAsync($"There is no backup named '{name}' — run !backups to list the available backups");
                return;
            }

            // The explicit confirm step: the first invocation only warns.
            if (arguments.Get(1) is not { } secondArgument)
            {
                await message.Channel.SendMessageAsync(
                    $"Restoring backup '{name}' will REPLACE ALL CURRENT DATA with the backup's contents. " +
                    $"The bot will be in maintenance mode during the restore. " +
                    $"If you are sure, run: !restorebackup {name} confirm");
                return;
            }

            if (secondArgument != "confirm")
            {
                await message.Channel.SendMessageAsync($"To actually restore, repeat the invocation with 'confirm' appended: !restorebackup {name} confirm");
                return;
            }

            await RestoreAsync(message, context, name, _backupManager, _maintenanceMode);
        }

        private async Task RestoreAsync(IMessage message, ICommandContext context, string name, IBackupManager backupManager, IMaintenanceMode maintenanceMode)
        {
            maintenanceMode.Enter();
            await TryPublishAsync($"Maintenance mode: restoring backup '{name}' — commands are temporarily unavailable.");

            try
            {
                // The pipeline's pooled connections point at the database being replaced; clearing
                // them lets pg_restore drop and recreate the objects (design §5.2).
                NpgsqlConnection.ClearAllPools();

                await backupManager.RestoreBackupAsync(name);

                // Caches derived from the database may hold pre-restore state.
                context.GuildSettings.InvalidateCache();

                await message.Channel.SendMessageAsync($"Backup '{name}' restored successfully. The bot is back in normal operation.");
                await TryPublishAsync($"Restore complete: backup '{name}' was restored; maintenance mode ended.");
            }
            catch (Exception)
            {
                // Exception details go to the log only — never to Discord.
                await message.Channel.SendMessageAsync("The restore failed — the database may be in an inconsistent state. " +
                    "Check the bot logs and consider scripts/restore-backup.sh for recovery.");
                await TryPublishAsync($"Restore FAILED for backup '{name}' — operator attention required; maintenance mode ended.");
            }
            finally
            {
                maintenanceMode.Exit();
            }
        }

        private async Task TryPublishAsync(string notification)
        {
            // Notifications must never affect the restore; the publisher never throws by contract,
            // but the restore must not depend on it.
            try
            {
                if (_notificationPublisher is not null)
                    await _notificationPublisher.PublishAsync(notification);
            }
            catch
            {
                // Never propagated: the restore outcome is reported in-channel regardless.
            }
        }
    }
}
