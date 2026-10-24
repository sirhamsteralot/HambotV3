using System;

namespace HambotV2.Commands
{
    /// <summary>
    /// Parses a Discord user argument that may be a raw numeric id or a mention (roadmap T7):
    /// Discord renders mentions as <c>&lt;@id&gt;</c> or the legacy <c>&lt;@!id&gt;</c>.
    /// </summary>
    internal static class DiscordUserId
    {
        public static bool TryParse(string argument, out ulong discordId)
        {
            discordId = 0;

            if (string.IsNullOrWhiteSpace(argument))
                return false;

            string trimmed = argument.Trim();
            if (ulong.TryParse(trimmed, out discordId))
                return true;

            if (trimmed.StartsWith("<@", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal))
            {
                string inner = trimmed.Substring(2, trimmed.Length - 3).Trim();
                if (inner.StartsWith("!"))
                    inner = inner[1..];

                return ulong.TryParse(inner, out discordId);
            }

            return false;
        }
    }
}
