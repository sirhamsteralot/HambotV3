using HambotV2.Backups;
using HambotV2.Commands.BackupCommands;
using HambotV2.Commands.LlmCommands;
using HambotV2.Commands.QuoteCommands;
using HambotV2.Commands.ReminderCommands;
using HambotV2.Commands.ResponseCommands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using HambotV2.Llm;
using HambotV2.Maintenance;
using HambotV2.QuoteOfTheDay;
using HambotV2.Scheduling;
using HambotV2.Watchdog;

namespace HambotV2.Commands
{
    /// <summary>
    /// The head of the command registration chain; new command groups plug in here. The optional
    /// dependencies (backup & stability, presence & profile identity) flow through from DI; they
    /// stay optional so the registry — and with it the registration contract tests — can be built
    /// without them (the affected commands then report "not configured" at execution time, which
    /// never happens in a DI-composed host).
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
            IHealthSnapshotProvider? healthSnapshotProvider = null,
            IPresenceService? presenceService = null,
            IImageContentDownloader? imageContentDownloader = null,
            ReminderSettings? reminderSettings = null,
            QuoteOfTheDaySettings? quoteOfTheDaySettings = null,
            QuoteSearchBacklog? quoteSearchBacklog = null,
            IGuildChannelLookup? guildChannelLookup = null,
            ILlmGate? llmGate = null,
            LlmSettings? llmSettings = null,
            ILlmUpdateService? llmUpdateService = null)
        {
            RegisterSystemCommands.RegisterCommands(commandService, notificationChannelStore, healthSnapshotProvider, presenceService, imageContentDownloader, guildChannelLookup);
            RegisterQuoteCommands.RegisterCommands(commandService, quoteSearchBacklog ?? new QuoteSearchBacklog(), quoteOfTheDaySettings);
            RegisterResponseCommands.RegisterCommands(commandService);
            RegisterReminderCommands.RegisterCommands(commandService, deliverySignal, reminderSettings);
            RegisterBackupCommands.RegisterCommands(commandService, backupManager, maintenanceMode, notificationPublisher);
            RegisterLlmCommands.RegisterCommands(commandService, llmGate, llmSettings, llmUpdateService);
        }
    }
}
