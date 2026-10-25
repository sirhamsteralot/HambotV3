using System;

namespace HambotV2.Commands
{
    /// <summary>
    /// Shared command-message parsing. Rules, also documented in software-design.md:
    /// the command identifier is matched case-sensitively as an exact prefix of the trimmed
    /// message; any run of whitespace separates tokens; the command name is the first token
    /// after the identifier, lowercased.
    /// </summary>
    internal static class CommandParsing
    {
        /// <summary>
        /// Extracts the lowercased command name from a message. Returns false for messages that
        /// are not commands at all, for identifier-only messages (e.g. a bare "!", possibly
        /// surrounded by whitespace), and for empty input.
        /// </summary>
        public static bool TryGetCommandName(string content, string identifier, out string commandName)
        {
            commandName = string.Empty;

            if (string.IsNullOrWhiteSpace(content) || string.IsNullOrEmpty(identifier))
                return false;

            string trimmed = content.Trim();
            if (!trimmed.StartsWith(identifier, StringComparison.Ordinal))
                return false;

            string remainder = trimmed.Substring(identifier.Length).Trim();
            if (remainder.Length == 0)
                return false;

            commandName = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            return commandName.Length > 0;
        }

        /// <summary>
        /// True when the message consists of the command identifier (and whitespace) only.
        /// Such messages are ignored silently instead of being answered like unknown commands.
        /// </summary>
        public static bool IsIdentifierOnly(string content, string identifier)
        {
            if (string.IsNullOrWhiteSpace(content) || string.IsNullOrEmpty(identifier))
                return false;

            string trimmed = content.Trim();
            return trimmed.StartsWith(identifier, StringComparison.Ordinal)
                && trimmed.Substring(identifier.Length).Trim().Length == 0;
        }
    }
}
