namespace HambotV2.LinkFix
{
    /// <summary>
    /// The shipped origin→mirror table: the only place a mirror host is written in code. Origins
    /// match case-insensitively and tolerate any subdomain; the short or opaque shapes
    /// (<c>redd.it/&lt;id&gt;</c>, <c>t.co/…</c>, <c>x.com/i/…</c>) and the equivalent mirrors are
    /// deliberately absent — an operator repoints or adds a row with <c>!linkfixmap</c>.
    /// </summary>
    internal static class LinkMappingDefaults
    {
        /// <summary>The shipped rows, one per origin host.</summary>
        public static IReadOnlyList<LinkMappingEntry> Entries { get; } =
        [
            new LinkMappingEntry("x.com", "fxtwitter.com", LinkPathShape.XStatus, LinkMappingLayer.ShippedDefault),
            new LinkMappingEntry("twitter.com", "fxtwitter.com", LinkPathShape.XStatus, LinkMappingLayer.ShippedDefault),
            new LinkMappingEntry("reddit.com", "redditez.com", LinkPathShape.RedditThread, LinkMappingLayer.ShippedDefault)
        ];

        /// <summary>The shipped row for an origin host, if the table has one.</summary>
        public static bool TryGetEntry(string originHost, out LinkMappingEntry entry)
        {
            foreach (LinkMappingEntry candidate in Entries)
            {
                if (string.Equals(candidate.OriginHost, originHost, StringComparison.OrdinalIgnoreCase))
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = null!;
            return false;
        }
    }
}
