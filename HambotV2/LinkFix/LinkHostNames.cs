using System.Diagnostics.CodeAnalysis;

namespace HambotV2.LinkFix
{
    /// <summary>
    /// Hostname validation shared by the <c>!linkfixmap</c> arguments, the mapping snapshot
    /// loader, and the rewriter: a bare hostname is trimmed and lowercased, then restricted to
    /// <c>[a-z0-9.-]</c>, at most 253 characters, at least two labels, no empty label, and no
    /// label starting or ending with a hyphen — so a scheme, path, port, userinfo, or wildcard
    /// never reaches the mapping.
    /// </summary>
    internal static class LinkHostNames
    {
        internal const int MaxLength = 253;

        public static bool TryNormalize(string? candidate, [NotNullWhen(true)] out string? host)
        {
            host = null;
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            string normalized = candidate.Trim().ToLowerInvariant();
            if (normalized.Length > MaxLength)
                return false;

            foreach (char character in normalized)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '.' && character != '-')
                    return false;
            }

            string[] labels = normalized.Split('.');
            if (labels.Length < 2)
                return false;

            foreach (string label in labels)
            {
                if (label.Length == 0 || label[0] == '-' || label[^1] == '-')
                    return false;
            }

            host = normalized;
            return true;
        }
    }
}
