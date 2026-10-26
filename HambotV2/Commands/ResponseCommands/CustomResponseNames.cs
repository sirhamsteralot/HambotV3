using System.Text.RegularExpressions;

namespace HambotV2.Commands.ResponseCommands
{
    /// <summary>
    /// The shared custom-response name rules: a single lowercase token of 1–32
    /// characters — lowercase letters, digits, hyphens, underscores. The pattern lives in this
    /// one helper so every write path validates names identically.
    /// </summary>
    internal static class CustomResponseNames
    {
        /// <summary>Maximum response name length.</summary>
        public const int MaxLength = 32;

        private static readonly Regex ValidName = new("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled);

        public static bool IsValid(string name) => ValidName.IsMatch(name);
    }
}
