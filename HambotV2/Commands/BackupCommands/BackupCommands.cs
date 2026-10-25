using Discord;
using HambotV2.Backups;

namespace HambotV2.Commands.BackupCommands
{
    /// <summary>
    /// Triggers an on-demand database backup (FR-96): the same dump format and shared directory as
    /// the scheduled backups, with the reply carrying the dump's name and size. Bot-owner tier by
    /// default, usable in direct messages (a global operation touching no guild state), with help
    /// text like every command. A failure produces an in-channel error and leaves the scheduled
    /// backup cycle (the sidecar) unaffected.
    /// </summary>
    internal sealed class BackupCommand : ICommand
    {
        private readonly IBackupManager? _backupManager;

        public BackupCommand(IBackupManager? backupManager = null)
        {
            _backupManager = backupManager;
        }

        public string Name => "backup";
        public string Description => "Creates a database backup immediately and replies with its name and size";
        public string Usage => "backup";
        public int DefaultPermissionTier => PermissionTier.BotOwner;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_backupManager is null)
            {
                await message.Channel.SendMessageAsync("Backups are not configured on this installation.");
                return;
            }

            try
            {
                BackupInfo backup = await _backupManager.CreateBackupAsync();
                await message.Channel.SendMessageAsync($"Backup created: {backup.Name} ({BackupSize.Format(backup.SizeBytes)})");
            }
            catch (Exception)
            {
                // The scheduled backup cycle is a separate sidecar; it is unaffected either way.
                await message.Channel.SendMessageAsync("The backup failed — check the bot logs for details.");
            }
        }
    }

    /// <summary>
    /// Lists the available backups — the dump files in the shared backup directory — newest first,
    /// capped at 10 entries (FR-97); an empty directory gets a friendly message instead of an error.
    /// Bot-owner tier by default, usable in direct messages.
    /// </summary>
    internal sealed class BackupsCommand : ICommand
    {
        /// <summary>Maximum entries in the list reply (FR-97).</summary>
        internal const int MaxListedEntries = 10;

        private readonly IBackupManager? _backupManager;

        public BackupsCommand(IBackupManager? backupManager = null)
        {
            _backupManager = backupManager;
        }

        public string Name => "backups";
        public string Description => "Lists the available database backups, newest first";
        public string Usage => "backups";
        public int DefaultPermissionTier => PermissionTier.BotOwner;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_backupManager is null)
            {
                await message.Channel.SendMessageAsync("Backups are not configured on this installation.");
                return;
            }

            IReadOnlyList<BackupInfo> backups = _backupManager.ListBackups();
            if (backups.Count == 0)
            {
                await message.Channel.SendMessageAsync("There are no backups yet.");
                return;
            }

            IEnumerable<string> lines = backups
                .Take(MaxListedEntries)
                .Select(backup => $"{backup.Timestamp:yyyy-MM-dd HH:mm} UTC — {backup.Name} ({BackupSize.Format(backup.SizeBytes)})");

            string hiddenNote = backups.Count > MaxListedEntries
                ? $"\n(and {backups.Count - MaxListedEntries} older backup{(backups.Count - MaxListedEntries == 1 ? string.Empty : "s")})"
                : string.Empty;

            await message.Channel.SendMessageAsync("Available backups:\n" + string.Join('\n', lines) + hiddenNote);
        }
    }
}
