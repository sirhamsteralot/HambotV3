using System.Diagnostics.CodeAnalysis;

namespace HambotV2.LinkFix
{
    /// <summary>
    /// The effective origin→mirror mapping of one moment: the shipped defaults with the runtime
    /// overrides applied, immutable once built. The rewriter resolves every message against one
    /// instance, so a <c>!linkfixmap</c> change applies from the next message without a restart
    /// or a cache to invalidate.
    /// </summary>
    internal sealed class LinkMapping
    {
        private readonly List<LinkMappingEntry> _entries;
        private readonly HashSet<string> _origins;
        private readonly HashSet<string> _mirrorHosts;

        /// <summary>The shipped table with no overrides.</summary>
        public static LinkMapping Shipped { get; } = new(Array.Empty<KeyValuePair<string, string>>());

        /// <param name="overrides">The stored <c>LinkFixMapping</c> rows: origin host → mirror host.</param>
        public LinkMapping(IEnumerable<KeyValuePair<string, string>> overrides)
        {
            // Only canonical rows count: a hand-edited row can never inject a scheme, path, or
            // port, and the effective map never disagrees with the table !linkfixmap clears.
            var validOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string origin, string mirror) in overrides)
            {
                if (IsCanonicalHostName(origin) && IsCanonicalHostName(mirror))
                    validOverrides[origin] = mirror;
            }

            _origins = new HashSet<string>(StringComparer.Ordinal);
            foreach (LinkMappingEntry shipped in LinkMappingDefaults.Entries)
                _origins.Add(shipped.OriginHost);
            foreach (string origin in validOverrides.Keys)
                _origins.Add(origin);

            // A mapping never points a mirror at an origin: the rewrite would send a link back to
            // the site it exists to escape.
            List<string> pointingAtOrigins = validOverrides.Keys
                .Where(origin => _origins.Contains(validOverrides[origin]))
                .ToList();
            foreach (string origin in pointingAtOrigins)
                validOverrides.Remove(origin);

            _mirrorHosts = new HashSet<string>(StringComparer.Ordinal);
            foreach (LinkMappingEntry shipped in LinkMappingDefaults.Entries)
                _mirrorHosts.Add(shipped.MirrorHost);
            foreach (string mirror in validOverrides.Values)
                _mirrorHosts.Add(mirror);

            // A mirror host is never an origin, so a rewritten link is never rewritten again.
            List<string> mirrorOrigins = validOverrides.Keys
                .Where(origin => _mirrorHosts.Contains(origin) && !LinkMappingDefaults.TryGetEntry(origin, out _))
                .ToList();
            foreach (string origin in mirrorOrigins)
                validOverrides.Remove(origin);

            _entries = new List<LinkMappingEntry>(LinkMappingDefaults.Entries.Count + validOverrides.Count);
            foreach (LinkMappingEntry shipped in LinkMappingDefaults.Entries)
            {
                _entries.Add(validOverrides.TryGetValue(shipped.OriginHost, out string? overrideHost)
                    ? shipped with { MirrorHost = overrideHost, Layer = LinkMappingLayer.Runtime }
                    : shipped);
            }

            // An override for an origin the shipped table lacks adds a row; the bot has no site
            // knowledge for it, so every path of that host qualifies.
            foreach ((string origin, string mirror) in validOverrides)
            {
                if (!LinkMappingDefaults.TryGetEntry(origin, out _))
                    _entries.Add(new LinkMappingEntry(origin, mirror, LinkPathShape.Any, LinkMappingLayer.Runtime));
            }

            _entries.Sort((left, right) => string.CompareOrdinal(left.OriginHost, right.OriginHost));
        }

        /// <summary>Every effective row, ordered by origin host.</summary>
        public IReadOnlyList<LinkMappingEntry> Entries => _entries;

        /// <summary>The effective row count — the figure the <c>LinkFix:MaxMappings</c> cap applies to.</summary>
        public int RowCount => _entries.Count;

        /// <summary>True when the host is one of the mirror hosts (shipped or runtime): such a link is never rewritten.</summary>
        public bool IsKnownMirrorHost(string host) => _mirrorHosts.Contains(host);

        /// <summary>True when the host is one of the origins (shipped or runtime): a mirror host may never be one.</summary>
        public bool IsKnownOrigin(string host) => _origins.Contains(host);

        /// <summary>
        /// The row whose origin matches the host — equal, or any subdomain of it (`www`, `mobile`,
        /// `m`, `old`, `new`, `np` and the rest) — preferring the most specific origin. A known
        /// mirror host never matches, so an already-mirrored link is left alone.
        /// </summary>
        public bool TryMatch(string host, [NotNullWhen(true)] out LinkMappingEntry? entry)
        {
            entry = null;
            if (_mirrorHosts.Contains(host))
                return false;

            foreach (LinkMappingEntry candidate in _entries)
            {
                bool matches = host.Equals(candidate.OriginHost, StringComparison.Ordinal)
                    || host.EndsWith("." + candidate.OriginHost, StringComparison.Ordinal);

                if (matches && (entry is null || candidate.OriginHost.Length > entry.OriginHost.Length))
                    entry = candidate;
            }

            return entry is not null;
        }

        private static bool IsCanonicalHostName(string value) =>
            LinkHostNames.TryNormalize(value, out string? normalized) && string.Equals(value, normalized, StringComparison.Ordinal);
    }
}
