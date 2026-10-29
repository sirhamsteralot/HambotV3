namespace HambotV2.Commands
{
    /// <summary>
    /// The shared fuzzy-matching primitive: a bounded two-row Levenshtein distance plus
    /// single-best selection. Every fuzzy consumer — command suggestions, quote search,
    /// member search — goes through here, each with its own qualification rule. Inputs are
    /// compared as given, so callers lowercase them; the bot's lowercase name contracts
    /// make the matching case-insensitive by construction.
    /// </summary>
    internal static class FuzzyMatcher
    {
        /// <summary>The edit distance between the two strings. Names and words are short, so no early-exit bounds are warranted.</summary>
        public static int Distance(string left, string right)
        {
            if (left.Length == 0)
                return right.Length;
            if (right.Length == 0)
                return left.Length;

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (int column = 0; column <= right.Length; column++)
                previous[column] = column;

            for (int row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (int column = 1; column <= right.Length; column++)
                {
                    int substitution = previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                    int insertion = previous[column] + 1;
                    int deletion = current[column - 1] + 1;
                    current[column] = Math.Min(substitution, Math.Min(insertion, deletion));
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[right.Length];
        }

        /// <summary>
        /// The best candidate by distance, ties broken alphabetically; only candidates the
        /// qualification rule accepts are considered. Null when none qualify.
        /// </summary>
        public static string? Best(string token, IEnumerable<string> candidates, Func<string, int, bool> qualifies)
        {
            string? best = null;
            int bestDistance = int.MaxValue;
            foreach (string candidate in candidates)
            {
                int distance = Distance(token, candidate);
                if (!qualifies(candidate, distance))
                    continue;

                if (distance < bestDistance || (distance == bestDistance && string.CompareOrdinal(candidate, best) < 0))
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            return best;
        }
    }
}
