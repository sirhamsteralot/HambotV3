using Discord;
using HambotV2.Connections;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Manages the bot's activity (FR-79): no args report it, `<type> <text...>` applies it, `clear`
    /// removes it. Tier 4, DM-usable; runtime changes are not persisted — a restart reapplies the
    /// startup configuration.
    /// </summary>
    internal sealed class PresenceCommand : ICommand
    {
        private readonly IPresenceService? _presenceService;

        public PresenceCommand(IPresenceService? presenceService = null)
        {
            _presenceService = presenceService;
        }

        public string Name => "presence";
        public string Description => "Shows, sets, or clears the bot's activity (playing/watching/listening/competing)";
        public string Usage => "presence [<type> <text...> | clear]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_presenceService is null)
            {
                await message.Channel.SendMessageAsync("Presence management is not configured on this installation.");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            string? firstArgument = arguments.Get(0);

            if (string.IsNullOrWhiteSpace(firstArgument))
            {
                PresenceActivity? activity = _presenceService.CurrentActivity;
                await message.Channel.SendMessageAsync(activity is null
                    ? "No activity is set."
                    : $"Current activity: {PresenceActivityTypes.GetLabel(activity.Type)} {activity.Text}");
                return;
            }

            if (firstArgument == "clear")
            {
                await SendApplyReplyAsync(message, await _presenceService.ClearActivityAsync(), "The activity was cleared.");
                return;
            }

            if (!PresenceActivityTypes.TryParse(firstArgument, out ActivityType type))
            {
                await message.Channel.SendMessageAsync(
                    $"Unknown activity type '{firstArgument}' — supported types: {string.Join(", ", PresenceActivityTypes.SupportedNames)}.");
                return;
            }

            string? text = arguments.GetJoined(1);
            if (string.IsNullOrWhiteSpace(text))
            {
                await message.Channel.SendMessageAsync(
                    $"The activity text is missing — usage: {context.CommandIdentifier}presence <type> <text...>");
                return;
            }

            if (text.Contains('\n') || text.Contains('\r'))
            {
                await message.Channel.SendMessageAsync("The activity text must be a single line.");
                return;
            }

            if (text.Length > ProfileLimits.MaxActivityTextLength)
            {
                await message.Channel.SendMessageAsync(
                    $"The activity text must be at most {ProfileLimits.MaxActivityTextLength} characters (got {text.Length}).");
                return;
            }

            bool applied = await _presenceService.ApplyActivityAsync(type, text);
            await SendApplyReplyAsync(message, applied,
                $"Activity set: {PresenceActivityTypes.GetLabel(type)} {text}.");
        }

        private static async Task SendApplyReplyAsync(IMessage message, bool applied, string successReply)
        {
            await message.Channel.SendMessageAsync(applied
                ? successReply
                : "Failed to update the activity — a Discord error occurred. Please try again in a moment.");
        }
    }
}
