using Discord;
using HambotV2.Connections;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// The snapshot of one executed quote search: the term as typed and every match, already
    /// ranked most relevant first (substring matches ahead of fuzzy ones), plus whether the
    /// relevance cap truncated the list. Pages render from the snapshot, so paging shows the
    /// search as it ran instead of re-querying and re-ranking on every click.
    /// </summary>
    internal sealed record QuoteSearchSnapshot(string Term, IReadOnlyList<DatabaseContext.Quote> Matches, bool Truncated);

    /// <summary>
    /// The in-memory backlog of the quote search pagination: each invoker's latest search snapshot
    /// under a search id the pagination buttons encode. One entry per invoker bounds memory; a
    /// new search replaces the previous one, and stale buttons name a search id nobody holds.
    /// </summary>
    internal sealed class QuoteSearchBacklog
    {
        private readonly object _syncRoot = new();
        private long _nextSearchId;
        private readonly Dictionary<ulong, (long SearchId, QuoteSearchSnapshot Snapshot)> _latestByInvoker = new();

        /// <summary>Keeps the snapshot as the invoker's current search and returns its id.</summary>
        public long Store(ulong invokerId, QuoteSearchSnapshot snapshot)
        {
            lock (_syncRoot)
            {
                long searchId = _nextSearchId++;
                _latestByInvoker[invokerId] = (searchId, snapshot);
                return searchId;
            }
        }

        /// <summary>True when the invoker's current search is exactly the paged one; a newer search replaces it.</summary>
        public bool TryGet(ulong invokerId, long searchId, out QuoteSearchSnapshot? snapshot)
        {
            lock (_syncRoot)
            {
                snapshot = null;
                return _latestByInvoker.TryGetValue(invokerId, out (long SearchId, QuoteSearchSnapshot Snapshot) current)
                    && current.SearchId == searchId
                    && (snapshot = current.Snapshot) is not null;
            }
        }
    }

    /// <summary>
    /// The interactive pagination of the quote search results: the search command stores the ranked
    /// match backlog and attaches a previous/next button pair whose custom ids encode the invoker,
    /// the search, and the target page. Paging is invoker-owned and renders from the snapshot, so a
    /// click needs no database access and never re-ranks the results.
    /// </summary>
    internal static class QuoteSearchPagination
    {
        internal const string CustomIdPrefix = "quotesearch:page:";

        /// <summary>The reply to a click by anyone but the user the search ran for.</summary>
        internal const string ForeignClickerReply = "Only the user who requested this search can page through it";

        /// <summary>The reply to a button of a search that is no longer its invoker's current one (or was lost to a restart).</summary>
        internal const string UnavailableSearchReply = "This search's results are no longer available — run the search again";

        /// <summary>How many matches one results page holds.</summary>
        internal const int PageSize = 10;

        /// <summary>How many of the ranked matches the backlog keeps — the cap that bounds a single invoker's snapshot.</summary>
        internal const int MaxResults = 100;

        /// <summary>The custom id of the button owned by <paramref name="invokerId"/> for search <paramref name="searchId"/> that opens <paramref name="page"/>.</summary>
        internal static string CustomId(ulong invokerId, long searchId, int page) => $"{CustomIdPrefix}{invokerId}:{searchId}:{page}";

        /// <summary>
        /// True when the custom id is a quote search pagination button; the out-parameters carry the
        /// owning invoker, the search, and the requested page.
        /// </summary>
        internal static bool TryGetTargetPage(string? customId, out ulong invokerId, out long searchId, out int targetPage)
        {
            invokerId = 0;
            searchId = 0;
            targetPage = 0;
            if (customId is null || !customId.StartsWith(CustomIdPrefix, StringComparison.Ordinal))
                return false;

            string remainder = customId[CustomIdPrefix.Length..];
            int firstSeparator = remainder.IndexOf(':');
            if (firstSeparator < 0)
                return false;

            int secondSeparator = remainder.IndexOf(':', firstSeparator + 1);
            return secondSeparator > firstSeparator
                && ulong.TryParse(remainder[..firstSeparator], out invokerId)
                && long.TryParse(remainder[(firstSeparator + 1)..secondSeparator], out searchId)
                && int.TryParse(remainder[(secondSeparator + 1)..], out targetPage)
                && targetPage >= 1;
        }

        /// <summary>The built previous/next button pair; null when the results fit one page.</summary>
        internal static MessageComponent? BuildButtons(ulong invokerId, long searchId, int currentPage, int pageCount)
        {
            return PaginationButtons.Build(CustomId(invokerId, searchId, currentPage - 1), CustomId(invokerId, searchId, currentPage + 1), currentPage, pageCount);
        }

        internal static int PageCount(int matchCount) => (int)Math.Ceiling(matchCount / (double)PageSize);

        /// <summary>
        /// One page of the snapshot: a header naming the term, the page, and the relevance cap, over
        /// the page's number-prefixed quote lines. Trimmed to Discord's message limit so the in-place
        /// modification never fails delivery either.
        /// </summary>
        internal static string RenderPage(QuoteSearchSnapshot search, int page)
        {
            int pageCount = PageCount(search.Matches.Count);
            var details = new List<string>();
            if (pageCount > 1)
                details.Add($"page {page} of {pageCount}");
            if (search.Truncated)
                details.Add($"showing the {MaxResults} most relevant");
            string header = details.Count > 0
                ? $"Quotes matching '{search.Term}' ({string.Join(", ", details)}):"
                : $"Quotes matching '{search.Term}':";

            string pageLines = string.Join(Environment.NewLine,
                search.Matches.Skip((page - 1) * PageSize).Take(PageSize).Select(QuoteCommand.FormatQuote));
            return QuoteCommand.TrimToMessageLimit(header + Environment.NewLine + pageLines);
        }

        /// <summary>
        /// Handles one pagination button click. A foreign clicker is declined ephemerally; a button of
        /// a search the backlog no longer holds gets the run-it-again reply; otherwise the snapshot's
        /// requested page rewrites the public message with rebuilt buttons.
        /// </summary>
        internal static async Task HandleClickAsync(IPaginationButton button, QuoteSearchBacklog backlog)
        {
            if (!TryGetTargetPage(button.CustomId, out ulong invokerId, out long searchId, out int targetPage))
                return;

            // Acknowledge within the interaction window; the reply below resolves the deferred state.
            await button.DeferAsync();

            if (button.User.Id != invokerId)
            {
                await button.SendEphemeralAsync(ForeignClickerReply);
                return;
            }

            if (!backlog.TryGet(invokerId, searchId, out QuoteSearchSnapshot? search) || search is null)
            {
                await button.SendEphemeralAsync(UnavailableSearchReply);
                return;
            }

            int pageCount = PageCount(search.Matches.Count);
            await button.ModifyListInPlaceAsync(RenderPage(search, targetPage), BuildButtons(invokerId, searchId, targetPage, pageCount));
        }
    }
}
