namespace HambotV2.Commands
{
    /// <summary>
    /// Parses a Discord channel argument that may be a raw numeric id or a mention:
    /// Discord renders channel mentions as <c>&lt;#id&gt;</c>.
    /// </summary>
    internal static class DiscordChannelId
    {
        public static bool TryParse(string? argument, out ulong channelId)
        {
            channelId = 0;

            if (string.IsNullOrWhiteSpace(argument))
                return false;

            string trimmed = argument.Trim();
            if (trimmed.StartsWith("<#", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal))
                trimmed = trimmed[2..^1];

            return ulong.TryParse(trimmed, out channelId) && channelId != 0;
        }
    }
}
