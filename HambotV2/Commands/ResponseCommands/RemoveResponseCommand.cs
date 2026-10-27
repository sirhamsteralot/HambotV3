using Discord;
using HambotV2.Connections;

namespace HambotV2.Commands.ResponseCommands
{
    /// <summary>
    /// Removes a custom response of the invoking guild by name; moderator by default,
    /// declining in direct messages. An unknown name produces an in-channel error.
    /// </summary>
    internal class RemoveResponseCommand : ICommand
    {
        public string Name => "removeresponse";
        public string Description => "Remove one of this server's custom responses by its name";
        public string Usage => "removeresponse <name>";
        public int DefaultPermissionTier => PermissionTier.Moderator;
        public CommandCategory Category => CommandCategory.CustomResponses;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Responses are per-guild state; the DM scope has no guild to remove from.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (!arguments.TryGetString(0, out string? name))
            {
                await message.Channel.SendMessageAsync($"Could not parse a response name from '{arguments.Get(0)}'. Usage: !{Usage}");
                return;
            }

            var response = await context.DbContext.CustomResponses.FindAsync(guildId, name);
            if (response is null)
            {
                await message.Channel.SendMessageAsync($"There is no response named '{name}' in this server");
                return;
            }

            context.DbContext.CustomResponses.Remove(response);
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Removed the response '{name}'");
        }
    }
}
