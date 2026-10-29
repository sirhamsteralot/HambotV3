using Discord;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// Case-insensitive quote search of the invoking guild: number-prefixed results,
    /// ordered by number, capped at 10. A quote also matches when one of its words is a
    /// near miss of the term (edit distance ≤ 2), so a misspelling still finds its quote;
    /// substring matches always rank ahead of fuzzy ones.
    /// </summary>
    internal class SearchQuotesCommand : ICommand
    {
        private const int MaxResults = 10;

        /// <summary>The word-level closeness rule of the fuzzy tier of the search.</summary>
        private const int MaxWordDistance = 2;

        public string Name => "searchquotes";
        public string Description => "Search this server's quotes for a text (up to 10 results)";
        public string Usage => "searchquotes <text...>";
        public int DefaultPermissionTier => PermissionTier.Everyone;
        public CommandCategory Category => CommandCategory.Quotes;

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

            // Fuzzy tier: when fewer than the cap matched, the remaining guild quotes are
            // searched word-wise for near misses, ranked by distance with the quote number
            // as the tiebreak — behind every substring match.
            if (matches.Count < MaxResults)
            {
                var substringNumbers = matches.Select(item => item.Number).ToHashSet();
                var fuzzy = (await context.DbContext.Quotes
                        .Where(item => item.GuildId == guildId)
                        .OrderBy(item => item.Number)
                        .ToListAsync())
                    .Where(item => !substringNumbers.Contains(item.Number))
                    .Select(item => (Quote: item, Distance: MinWordDistance(item.Text, lowercasedTerm)))
                    .Where(match => match.Distance <= MaxWordDistance)
                    .OrderBy(match => match.Distance).ThenBy(match => match.Quote.Number)
                    .Select(match => match.Quote)
                    .Take(MaxResults - matches.Count);
                matches.AddRange(fuzzy);
            }

            if (matches.Count == 0)
            {
                await message.Channel.SendMessageAsync($"No quotes match '{term}' in this server");
                return;
            }

            await QuoteCommand.SendFittedAsync(message, string.Join("\n", matches.Select(QuoteCommand.FormatQuote)));
        }

        /// <summary>The smallest distance between the term and any whitespace-delimited word of the quote.</summary>
        private static int MinWordDistance(string text, string lowercasedTerm)
        {
            return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Min(word => FuzzyMatcher.Distance(word.ToLowerInvariant(), lowercasedTerm));
        }
    }
}
