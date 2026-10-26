using Discord;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.ResponseCommands
{
    /// <summary>
    /// Lists the invoking guild's custom response names alphabetically; everyone by
    /// default, declining in direct messages.
    /// </summary>
    internal class ListResponsesCommand : ICommand
    {
        public string Name => "listresponses";
        public string Description => "List this server's custom response names";
        public string Usage => "listresponses";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Responses are per-guild state; the DM scope has no guild to list.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var names = await context.DbContext.CustomResponses
                .Where(response => response.GuildId == guildId)
                .OrderBy(response => response.Name)
                .Select(response => response.Name)
                .ToListAsync();

            if (names.Count == 0)
            {
                await message.Channel.SendMessageAsync("There are no custom responses in this server yet — add one with !addresponse <name> <content>");
                return;
            }

            await message.Channel.SendMessageAsync($"Custom responses: {string.Join(", ", names)}");
        }
    }
}
