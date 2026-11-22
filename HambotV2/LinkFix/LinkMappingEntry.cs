namespace HambotV2.LinkFix
{
    /// <summary>
    /// The eligibility rule an origin's URLs must satisfy before they are rewritten: each
    /// mapping entry names its shape, so the rewriter applies the predicate from the table
    /// instead of branching per site. An operator-added origin gets <see cref="Any"/>.
    /// </summary>
    internal enum LinkPathShape
    {
        /// <summary>Any path qualifies — operator-added origins, whose site the bot knows nothing about.</summary>
        Any,

        /// <summary>X/Twitter post paths only: <c>/&lt;user&gt;/status/&lt;digits&gt;</c>, never the site's own routes.</summary>
        XStatus,

        /// <summary>Reddit thread paths only: <c>/r/&lt;sub&gt;/comments/&lt;id&gt;/…</c>.</summary>
        RedditComments
    }

    /// <summary>The layer a mapping entry's mirror host comes from.</summary>
    internal enum LinkMappingLayer
    {
        /// <summary>The host shipped in <see cref="LinkMappingDefaults"/>.</summary>
        ShippedDefault,

        /// <summary>The host the bot owner set with <c>!linkfixmap</c>.</summary>
        Runtime
    }

    /// <summary>
    /// One effective origin→mirror row: the origin host it matches (subdomain-tolerant), the
    /// mirror host its URLs are rewritten to, the path shape those URLs must have, and the layer
    /// the mirror host came from.
    /// </summary>
    internal sealed record LinkMappingEntry(string OriginHost, string MirrorHost, LinkPathShape Shape, LinkMappingLayer Layer);
}
