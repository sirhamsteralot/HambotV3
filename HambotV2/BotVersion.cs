using System.Reflection;

namespace HambotV2
{
    /// <summary>
    /// The bot's version (FR-76): the raw release tag and commit hash embedded at build time
    /// by the MSBuild target in <c>HambotV2.csproj</c>, formatted into the reported version
    /// string by <see cref="Format"/> — pure string logic, separately unit-tested (NFR-17).
    /// It is the single source for the <c>!version</c> command and the startup log line
    /// (§3.9), so the two can never disagree. The metadata lives in this assembly (not the
    /// entry assembly), so the value is correct however the bot is hosted.
    /// </summary>
    internal static class BotVersion
    {
        /// <summary>Semver reported when no reachable release tag identifies the build (FR-76).</summary>
        internal const string UntaggedSemver = "0.0.0";

        /// <summary>Commit identifier reported when no git metadata reached the build (FR-76).</summary>
        internal const string UnknownCommit = "unknown";

        private static string? _current;

        /// <summary>The formatted version string, e.g. "1.2.3+1639324" — computed once, cached.</summary>
        public static string Current => _current ??= ReadCurrent();

        private static string ReadCurrent()
        {
            string? tag = null;
            string? commit = null;

            foreach (AssemblyMetadataAttribute metadata in typeof(BotVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                switch (metadata.Key)
                {
                    case "BotVersionTag":
                        tag = metadata.Value;
                        break;
                    case "BotVersionCommit":
                        commit = metadata.Value;
                        break;
                }
            }

            return Format(tag, commit);
        }

        /// <summary>
        /// Pure tag→version formatting (NFR-17): strips the tag's "v" prefix and joins it with
        /// the commit hash as semver build metadata. Fallbacks per FR-76: no tag →
        /// <see cref="UntaggedSemver"/>, no commit hash → <see cref="UnknownCommit"/> — so a
        /// build never misidentifies its code as a released one. Inputs are trimmed because
        /// MSBuild's Exec output capture can carry trailing line-ending characters.
        /// </summary>
        internal static string Format(string? tag, string? commitHash)
        {
            string trimmedTag = (tag ?? string.Empty).Trim().TrimStart('v');
            string trimmedCommit = (commitHash ?? string.Empty).Trim();

            if (trimmedTag.Length == 0)
                trimmedTag = UntaggedSemver;
            if (trimmedCommit.Length == 0)
                trimmedCommit = UnknownCommit;

            return $"{trimmedTag}+{trimmedCommit}";
        }
    }
}
