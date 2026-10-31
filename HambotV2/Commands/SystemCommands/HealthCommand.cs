using Discord;
using HambotV2.SlashCommands;
using HambotV2.Watchdog;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Reports the bot's operational state: build version, uptime, gateway state, database
    /// reachability, backup and restore-test status, pending reminder count, the configured
    /// notification channel, and the MCP endpoint state — one in-channel report from the single
    /// health snapshot provider. Bot-owner tier by default, usable in direct messages
    /// (no guild state is read).
    /// </summary>
    internal sealed class HealthCommand : ICommand
    {
        private readonly IHealthSnapshotProvider? _healthProvider;

        public HealthCommand(IHealthSnapshotProvider? healthProvider = null)
        {
            _healthProvider = healthProvider;
        }

        public string Name => "health";
        public string Description => "Reports the bot's operational state (version, gateway, database, backups, reminders)";
        public string Usage => "health";
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.BackupsAndStability;

        /// <summary>Slash surface: administrative, no arguments.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand => ApplicationCommandDeclaration.NoArguments;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_healthProvider is null)
            {
                await message.Channel.SendMessageAsync("Health reporting is not configured on this installation.");
                return;
            }

            HealthSnapshot snapshot = await _healthProvider.CaptureAsync();

            await message.Channel.SendMessageAsync(
                "Bot health report:\n" +
                $"Version: {snapshot.Version}\n" +
                $"Uptime: {FormatUptime(snapshot.Uptime)}\n" +
                $"Gateway: {snapshot.GatewayState}\n" +
                $"Database: {snapshot.Database}\n" +
                $"Last backup: {snapshot.Backup}\n" +
                $"Last restore test: {snapshot.RestoreTest}\n" +
                $"Pending reminders: {snapshot.PendingReminders}\n" +
                $"Notification channel: {snapshot.NotificationChannel}\n" +
                $"MCP: {snapshot.Mcp}\n" +
                $"LLM: {snapshot.Llm}");
        }

        private static string FormatUptime(TimeSpan uptime) => uptime.Days > 0
            ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
            : uptime.Hours > 0
                ? $"{uptime.Hours}h {uptime.Minutes}m"
                : $"{uptime.Minutes}m";
    }
}
