using Discord;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// Case-insensitive quote search of the invoking guild: number-prefixed results,
    /// ordered by number, capped at 10.
    /// </summary>
    internal class SearchQuotesCommand : ICommand
    {
        private const int MaxResults = 10;

        public string Name => "searchquotes";
        public string Description => "Search this server's quotes for a text (up to 10 results)";
        public string Usage => "searchquotes <text...>";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Quotes are per-guild state; the DM scope has no guild to search.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            if (arguments.GetJoined(0) is not string term)
            {
                await message.Channel.SendMessageAsync($"The search text is missing. Usage: !{Usage}");
                return;
            }

            // ToLower().Contains() translates on both Npgsql and the InMemory test provider.
            string lowercasedTerm = term.ToLowerInvariant();
            var matches = await context.DbContext.Quotes
                .Where(item => item.GuildId == guildId && item.Text.ToLower().Contains(lowercasedTerm))
                .OrderBy(item => item.Number)
                .Take(MaxResults)
                .ToListAsync();

            if (matches.Count == 0)
            {
                await message.Channel.SendMessageAsync($"No quotes match '{term}' in this server");
                return;
            }

            await QuoteCommand.SendFittedAsync(message, string.Join("\n", matches.Select(QuoteCommand.FormatQuote)));
        }
    }
}
