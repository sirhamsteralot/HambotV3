using System.Text;

namespace HambotV2.LinkFix
{
    /// <summary>
    /// The purely textual rewrite: message text plus the effective mapping in, the distinct
    /// rewritten URLs out. Nothing is fetched, resolved, or validated — a rewrite is a host swap,
    /// because every mirror serves the origin's own path. Only URLs are ever written into the
    /// repost; the rest of the message is not touched.
    /// </summary>
    internal static class LinkRewriter
    {
        /// <summary>Discord's message limit: the repost is one message and is never split.</summary>
        internal const int MaxRepostLength = 2000;

        private const string HttpsScheme = "https://";
        private const string HttpScheme = "http://";

        /// <summary>X's own routes: they look like <c>/&lt;user&gt;/status/&lt;id&gt;</c> but are not posts.</summary>
        private static readonly string[] XReservedFirstSegments = ["i", "intent", "share", "search", "hashtag"];

        /// <summary>
        /// The textual gate: true when the message carries at least one URL whose host is an
        /// origin of the mapping — so an opted-in guild's database is never read for an ordinary
        /// message.
        /// </summary>
        public static bool HasMappableLink(string content, LinkMapping mapping)
        {
            foreach (LinkUrl link in EnumerateLinks(content))
            {
                if (mapping.TryMatch(link.Host, out _))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Rewrites every eligible URL: scheme forced to <c>https</c>, host replaced by the
        /// origin's mirror host, path, query, and fragment kept verbatim; duplicates collapse on
        /// the rewritten URL, first appearance wins the order, and the tail is dropped past
        /// <paramref name="maxLinks"/> or Discord's message limit.
        /// </summary>
        public static IReadOnlyList<string> Rewrite(string content, LinkMapping mapping, int maxLinks)
        {
            var rewritten = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var text = new StringBuilder();

            foreach (LinkUrl link in EnumerateLinks(content))
            {
                if (rewritten.Count >= maxLinks)
                    break;

                if (!mapping.TryMatch(link.Host, out LinkMappingEntry? entry) || !MatchesPathShape(entry.Shape, link.Path))
                    continue;

                string url = $"{HttpsScheme}{entry.MirrorHost}{link.Path}{link.Query}{link.Fragment}";
                if (!seen.Add(url))
                    continue;

                int separatorLength = rewritten.Count == 0 ? 0 : 1;
                if (text.Length + separatorLength + url.Length > MaxRepostLength)
                    break;

                if (separatorLength > 0)
                    text.Append('\n');

                text.Append(url);
                rewritten.Add(url);
            }

            return rewritten;
        }

        /// <summary>Every <c>http://</c>/<c>https://</c> run of non-whitespace characters, trailing punctuation stripped.</summary>
        private static IEnumerable<LinkUrl> EnumerateLinks(string content)
        {
            int index = 0;
            while (index < content.Length)
            {
                int schemeLength = SchemeLengthAt(content, index);
                if (schemeLength == 0)
                {
                    index++;
                    continue;
                }

                int end = index + schemeLength;
                while (end < content.Length && !char.IsWhiteSpace(content[end]))
                    end++;

                string run = content[index..end].TrimEnd('.', ',', ')', ']', '>');
                index = end;

                if (run.Length > schemeLength && TryParse(run, schemeLength, out LinkUrl link))
                    yield return link;
            }
        }

        private static int SchemeLengthAt(string content, int index)
        {
            if (MatchesScheme(content, index, HttpsScheme))
                return HttpsScheme.Length;

            if (MatchesScheme(content, index, HttpScheme))
                return HttpScheme.Length;

            return 0;
        }

        private static bool MatchesScheme(string content, int index, string scheme)
        {
            return index + scheme.Length <= content.Length
                && content.AsSpan(index, scheme.Length).Equals(scheme, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Splits one URL run into the parts the rewrite keeps verbatim.</summary>
        private static bool TryParse(string run, int schemeLength, out LinkUrl link)
        {
            link = default;
            string remainder = run[schemeLength..];

            int authorityEnd = remainder.IndexOfAny(['/', '?', '#']);
            string authority = authorityEnd < 0 ? remainder : remainder[..authorityEnd];
            string rest = authorityEnd < 0 ? string.Empty : remainder[authorityEnd..];

            if (authority.LastIndexOf('@') is int userInfoEnd and >= 0)
                authority = authority[(userInfoEnd + 1)..];

            if (authority.LastIndexOf(':') is int portStart and >= 0)
                authority = authority[..portStart];

            if (authority.Length == 0)
                return false;

            int fragmentStart = rest.IndexOf('#');
            string fragment = fragmentStart < 0 ? string.Empty : rest[fragmentStart..];
            string beforeFragment = fragmentStart < 0 ? rest : rest[..fragmentStart];

            int queryStart = beforeFragment.IndexOf('?');
            string query = queryStart < 0 ? string.Empty : beforeFragment[queryStart..];
            string path = queryStart < 0 ? beforeFragment : beforeFragment[..queryStart];

            link = new LinkUrl(authority.ToLowerInvariant(), path, query, fragment);
            return true;
        }

        private static bool MatchesPathShape(LinkPathShape shape, string path) => shape switch
        {
            LinkPathShape.Any => true,
            LinkPathShape.XStatus => IsXStatusPath(path),
            LinkPathShape.RedditThread => IsRedditThreadPath(path),
            _ => false
        };

        private static bool IsXStatusPath(string path)
        {
            string[] segments = SplitPath(path);
            if (segments.Length != 3)
                return false;

            if (!segments[1].Equals("status", StringComparison.OrdinalIgnoreCase)
                || !segments[2].All(char.IsAsciiDigit))
            {
                return false;
            }

            return !XReservedFirstSegments.Contains(segments[0], StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsRedditThreadPath(string path)
        {
            // The thread path, plus the app's share form (/r/<sub>/s/<id>): the share token is
            // opaque only to us — the mirror resolves it, so the rewrite stays a host swap.
            string[] segments = SplitPath(path);
            return segments.Length >= 4
                && segments[0].Equals("r", StringComparison.OrdinalIgnoreCase)
                && (segments[2].Equals("comments", StringComparison.OrdinalIgnoreCase)
                    || segments[2].Equals("s", StringComparison.OrdinalIgnoreCase));
        }

        private static string[] SplitPath(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>One URL run, split into the parts the rewrite keeps verbatim; <see cref="Host"/> is lowercase.</summary>
        private readonly record struct LinkUrl(string Host, string Path, string Query, string Fragment);
    }
}
