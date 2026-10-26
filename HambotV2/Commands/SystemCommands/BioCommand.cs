using Discord;
using HambotV2.Connections;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Manages the bot's profile "About Me": no args report it, `<text...>` applies it
    /// (≤ 190 chars, line breaks preserved verbatim — the bio is per-guild state, so it applies in
    /// every member guild), `clear` removes it. Tier 4, DM-usable; runtime changes are not
    /// persisted, and an absent startup configuration never wipes a hand-set bio.
    /// </summary>
    internal sealed class BioCommand : ICommand
    {
        private readonly IPresenceService? _presenceService;

        public BioCommand(IPresenceService? presenceService = null)
        {
            _presenceService = presenceService;
        }

        public string Name => "bio";
        public string Description => "Shows, sets, or clears the bot's profile \"About Me\" text";
        public string Usage => "bio [<text...> | clear]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;

        /// <summary>
        /// Slash surface: the bio text stays free-form so <c>clear</c> and multi-line
        /// text parse exactly as on the text surface.
        /// </summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show, the new bio text, or clear");

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
                string? bio = _presenceService.CurrentBio;
                await message.Channel.SendMessageAsync(string.IsNullOrEmpty(bio)
                    ? "No bio is set."
                    : $"Current bio:\n{bio}");
                return;
            }

            if (firstArgument == "clear")
            {
                await SendApplyReplyAsync(message, await _presenceService.ClearBioAsync(), "The bio was cleared.");
                return;
            }

            // The first argument is known non-empty here, so the raw remainder carries it.
            string text = arguments.GetRaw()!;

            if (text.Length > ProfileLimits.MaxBioLength)
            {
                await message.Channel.SendMessageAsync(
                    $"The bio must be at most {ProfileLimits.MaxBioLength} characters (got {text.Length}).");
                return;
            }

            bool applied = await _presenceService.ApplyBioAsync(text);
            await SendApplyReplyAsync(message, applied, $"The bio was set to:\n{text}");
        }

        private static async Task SendApplyReplyAsync(IMessage message, bool applied, string successReply)
        {
            await message.Channel.SendMessageAsync(applied
                ? successReply
                : "Failed to update the bio — a Discord error occurred. Please try again in a moment.");
        }
    }
}
