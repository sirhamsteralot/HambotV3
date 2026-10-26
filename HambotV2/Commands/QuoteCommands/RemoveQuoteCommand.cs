using Discord;
using HambotV2.Connections;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// Removes a quote of the invoking guild by its number; moderator by default.
    /// Removal never renumbers the remaining quotes.
    /// </summary>
    internal class RemoveQuoteCommand : ICommand
    {
        public string Name => "removequote";
        public string Description => "Remove one of this server's quotes by its number";
        public string Usage => "removequote <number>";
        public int DefaultPermissionTier => PermissionTier.Moderator;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Quotes are per-guild state; the DM scope has no guild to remove from.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (!arguments.TryGetInt(0, out int number))
            {
                await message.Channel.SendMessageAsync($"Could not parse a quote number from '{arguments.Get(0)}'. Usage: !{Usage}");
                return;
            }

            var quote = await context.DbContext.Quotes.FindAsync(guildId, number);
            if (quote is null)
            {
                await message.Channel.SendMessageAsync($"There is no quote with number {number} in this server");
                return;
            }

            context.DbContext.Quotes.Remove(quote);
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Removed quote #{number}");
        }
    }
}
