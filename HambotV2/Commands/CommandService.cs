using System.Diagnostics.CodeAnalysis;
using HambotV2.Backups;
using HambotV2.Connections;
using HambotV2.Maintenance;
using HambotV2.Scheduling;
using HambotV2.Watchdog;

namespace HambotV2.Commands
{
    internal class CommandService : ICommandService
    {
        /// <summary>Shared with the scheduled-message commands and the delivery loops; production DI supplies the one singleton.</summary>
        private readonly IScheduledDeliverySignal _deliverySignal;

        /// <summary>Backup & stability dependencies (§3.13/§5.2), resolved by production DI and passed through the registration chain.</summary>
        private readonly IBackupManager? _backupManager;
        private readonly IMaintenanceMode? _maintenanceMode;
        private readonly INotificationPublisher? _notificationPublisher;
        private readonly INotificationChannelStore? _notificationChannelStore;
        private readonly IHealthSnapshotProvider? _healthSnapshotProvider;

        /// <summary>Presence & profile identity dependencies (§3.10), same treatment as the backup & stability set.</summary>
        private readonly IPresenceService? _presenceService;
        private readonly IImageContentDownloader? _imageContentDownloader;

        /// <summary>
        /// The signal defaults to an isolated instance, and the backup & stability and presence dependencies to null, which
        /// keeps the registry usable in tests that do not exercise those features (the affected
        /// commands then report "not configured" if actually executed).
        /// </summary>
        public CommandService(
            IScheduledDeliverySignal? deliverySignal = null,
            IBackupManager? backupManager = null,
            IMaintenanceMode? maintenanceMode = null,
            INotificationPublisher? notificationPublisher = null,
            INotificationChannelStore? notificationChannelStore = null,
            IHealthSnapshotProvider? healthSnapshotProvider = null,
            IPresenceService? presenceService = null,
            IImageContentDownloader? imageContentDownloader = null)
        {
            _deliverySignal = deliverySignal ?? new ScheduledDeliverySignal();
            _backupManager = backupManager;
            _maintenanceMode = maintenanceMode;
            _notificationPublisher = notificationPublisher;
            _notificationChannelStore = notificationChannelStore;
            _healthSnapshotProvider = healthSnapshotProvider;
            _presenceService = presenceService;
            _imageContentDownloader = imageContentDownloader;
        }

        public Dictionary<string, ICommand> Commands { get; } = new();

        /// <summary>True once the built-in commands have been registered; guards the lazy registration in <see cref="TryGetCommand"/>.</summary>
        public bool CommandsRegistered { get; private set; }

        public void RegisterCommands()
        {
            RegisterAllCommands.RegisterCommands(this, _deliverySignal, _backupManager, _maintenanceMode, _notificationPublisher, _notificationChannelStore, _healthSnapshotProvider, _presenceService, _imageContentDownloader);
            CommandsRegistered = true;
        }

        public void RegisterCommand(string commandName, ICommand command)
        {
            // The single chokepoint for the command contract: a command without help text, with a
            // non-lowercase name, or with an invalid default tier never enters the registry.
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentException("Command names must not be empty.", nameof(commandName));

            if (commandName != commandName.ToLowerInvariant())
                throw new ArgumentException($"Command name '{commandName}' must be lowercase (lookup lowercases input).", nameof(commandName));

            if (string.IsNullOrWhiteSpace(command.Description))
                throw new ArgumentException($"Command '{commandName}' must declare a non-empty Description for the help system.", nameof(command));

            if (string.IsNullOrWhiteSpace(command.Usage))
                throw new ArgumentException($"Command '{commandName}' must declare a non-empty Usage for the help system.", nameof(command));

            if (!PermissionTier.IsValid(command.DefaultPermissionTier))
                throw new ArgumentException($"Command '{commandName}' declares invalid default permission tier {command.DefaultPermissionTier}.", nameof(command));

            Commands.Add(commandName, command);
        }

        public bool TryGetCommand(string content, string identifier, [NotNullWhen(true)] out ICommand? command)
        {
            if (!CommandsRegistered)
                RegisterCommands();

            command = null;
            return CommandParsing.TryGetCommandName(content, identifier, out string commandName)
                && Commands.TryGetValue(commandName, out command);
        }

        public bool IsIdentifierOnlyMessage(string content, string identifier)
        {
            return CommandParsing.IsIdentifierOnly(content, identifier);
        }
    }
}
