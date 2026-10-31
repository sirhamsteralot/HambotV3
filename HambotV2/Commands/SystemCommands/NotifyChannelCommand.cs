using Discord;
using HambotV2.SlashCommands;
using HambotV2.Watchdog;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Manages the operator notification channel: where errors, failures, and criticals
    /// are posted. No arguments report the current channel (or that none is set);
    /// a channel mention or raw channel id sets it and persists the setting across restarts in the
    /// global `BotSetting` storage; `clear` unsets it. Global bot-owner scope (tier 4 by default),
    /// usable in direct messages, with help text.
    /// </summary>
    internal sealed class NotifyChannelCommand : ICommand
    {
        private readonly INotificationChannelStore? _channelStore;

        public NotifyChannelCommand(INotificationChannelStore? channelStore = null)
        {
            _channelStore = channelStore;
        }

        public string Name => "notifychannel";
        public string Description => "Shows, sets, or clears the channel the bot posts error and failure notifications to";
        public string Usage => "notifychannel [<channel mention or id> | clear]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.BackupsAndStability;

        /// <summary>
        /// Slash surface: the operation stays free-form so <c>clear</c> and a raw
        /// channel id parse exactly as on the text surface.
        /// </summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show, a channel mention or id, or clear");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_channelStore is null)
            {
                await message.Channel.SendMessageAsync("Notifications are not configured on this installation.");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            string? firstArgument = arguments.Get(0);
            if (string.IsNullOrWhiteSpace(firstArgument))
            {
                await ShowCurrentChannelAsync(message, _channelStore);
                return;
            }

            if (firstArgument == "clear")
            {
                await _channelStore.SetChannelIdAsync(null);
                await message.Channel.SendMessageAsync("The notification channel was cleared — notifications go to logs and bot-owner DMs only.");
                return;
            }

            if (!TryParseChannelId(firstArgument, out ulong channelId))
            {
                await message.Channel.SendMessageAsync("That does not look like a channel — pass a channel mention (e.g. #ops) or a raw channel id.");
                return;
            }

            await _channelStore.SetChannelIdAsync(channelId);
            await message.Channel.SendMessageAsync($"Notifications will now be posted to <#{channelId}>; the setting persists across restarts.");
        }

        private static async Task ShowCurrentChannelAsync(IMessage message, INotificationChannelStore channelStore)
        {
            ulong? channelId = await channelStore.GetChannelIdAsync();
            await message.Channel.SendMessageAsync(channelId is ulong channel
                ? $"Notifications are currently posted to <#{channel}> ({channel})."
                : "No notification channel is set — notifications go to logs and bot-owner DMs only.");
        }

        /// <summary>Accepts a raw channel id and a channel mention (`<#id>`).</summary>
        private static bool TryParseChannelId(string token, out ulong channelId) => DiscordChannelId.TryParse(token, out channelId);
    }
}
