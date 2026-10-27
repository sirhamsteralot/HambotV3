using Discord;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Replies in-channel with the bot's version: the release tag's semver plus the
    /// build commit's short hash (e.g. <c>1.2.3+1639324</c>), from the same single source as
    /// the startup log line — the <see cref="BotVersion"/> provider. No guild state is
    /// read, so it works in direct messages.
    /// </summary>
    internal class VersionCommand : ICommand
    {
        public string Name => "version";
        public string Description => "Shows the bot's version";
        public string Usage => "version";
        public int DefaultPermissionTier => PermissionTier.Everyone;
        public CommandCategory Category => CommandCategory.General;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            await message.Channel.SendMessageAsync(BotVersion.Current);
        }
    }
}
