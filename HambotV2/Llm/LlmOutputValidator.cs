using HambotV2.Connections;

namespace HambotV2.Llm
{
    /// <summary>
    /// Per-surface output rules: the model's reply must fit the target surface before it is
    /// applied. Invalid output is rejected — the current state stays and the next tick retries.
    /// </summary>
    internal static class LlmOutputValidator
    {
        /// <summary>Presence: non-empty, single line, at most 128 characters.</summary>
        public static bool TryValidatePresence(string? output, out string value)
        {
            value = (output ?? string.Empty).Trim();
            return value.Length > 0
                && !value.Contains('\n')
                && !value.Contains('\r')
                && value.Length <= ProfileLimits.MaxActivityTextLength;
        }

        /// <summary>Bio: non-empty, at most 190 characters, line breaks allowed.</summary>
        public static bool TryValidateBio(string? output, out string value)
        {
            value = (output ?? string.Empty).Trim();
            return value.Length > 0 && value.Length <= ProfileLimits.MaxBioLength;
        }
    }
}
