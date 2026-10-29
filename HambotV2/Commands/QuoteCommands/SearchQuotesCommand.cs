using Discord;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// Case-insensitive quote search of the invoking guild, ranked most relevant first: every
    /// substring match ordered by quote number, then every quote one of whose words is a near
    /// miss of the term (edit distance ≤ 2), ranked by distance with the quote number as the
    /// tiebreak. The ranked backlog is kept per invoker so the previous/next buttons page
    /// through all matches down to the relevance cap.
    /// </summary>
    internal class SearchQuotesCommand : ICommand
    {
        /// <summary>The word-level closeness rule of the fuzzy tier of the search.</summary>
        private const int MaxWordDistance = 2;

        private readonly QuoteSearchBacklog _backlog;

        public SearchQuotesCommand(QuoteSearchBacklog backlog)
        {
            _backlog = backlog;
        }

        public string Name => "searchquotes";
        public string Description => "Search this server's quotes for a text, most relevant first, paged with buttons";
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
            var substringMatches = await context.DbContext.Quotes
                .Where(item => item.GuildId == guildId && item.Text.ToLower().Contains(lowercasedTerm))
                .OrderBy(item => item.Number)
                .ToListAsync();

            // Fuzzy tier: the remaining guild quotes are searched word-wise for near misses, ranked
            // by distance with the quote number as the tiebreak — behind every substring match.
            var substringNumbers = substringMatches.Select(item => item.Number).ToHashSet();
            var fuzzyMatches = (await context.DbContext.Quotes
                    .Where(item => item.GuildId == guildId)
                    .OrderBy(item => item.Number)
                    .ToListAsync())
                .Where(item => !substringNumbers.Contains(item.Number))
                .Select(item => (Quote: item, Distance: MinWordDistance(item.Text, lowercasedTerm)))
                .Where(match => match.Distance <= MaxWordDistance)
                .OrderBy(match => match.Distance).ThenBy(match => match.Quote.Number)
                .Select(match => match.Quote)
                .ToList();

            var matches = substringMatches.Concat(fuzzyMatches).ToList();
            bool truncated = matches.Count > QuoteSearchPagination.MaxResults;
            if (truncated)
                matches = matches.Take(QuoteSearchPagination.MaxResults).ToList();

            if (matches.Count == 0)
            {
                await message.Channel.SendMessageAsync($"No quotes match '{term}' in this server");
                return;
            }

            var search = new QuoteSearchSnapshot(term, matches, truncated);
            long searchId = _backlog.Store(message.Author.Id, search);
            await QuoteCommand.SendFittedAsync(
                message,
                QuoteSearchPagination.RenderPage(search, page: 1),
                QuoteSearchPagination.BuildButtons(message.Author.Id, searchId, currentPage: 1, QuoteSearchPagination.PageCount(matches.Count)));
        }

        /// <summary>The smallest distance between the term and any whitespace-delimited word of the quote.</summary>
        private static int MinWordDistance(string text, string lowercasedTerm)
        {
            return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Min(word => FuzzyMatcher.Distance(word.ToLowerInvariant(), lowercasedTerm));
        }
    }
}
