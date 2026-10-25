namespace HambotV2.Commands
{
    /// <summary>
    /// Shared argument parsing for command implementations (NFR-13): a command
    /// receives the message as typed values instead of hand-rolled Split/TryParse code.
    ///
    /// Parsing follows the pipeline's rules (see <see cref="CommandParsing"/>): the message is
    /// split on runs of spaces, and the first token (command identifier + command name) is
    /// dropped — index 0 is the command's first argument. Quoting is not supported; every
    /// whitespace-separated token is one argument.
    /// </summary>
    internal sealed class CommandArguments
    {
        private readonly string _content;
        private readonly string[] _tokens;

        private CommandArguments(string content, string[] tokens)
        {
            _content = content;
            _tokens = tokens;
        }

        /// <summary>Number of arguments after the command token.</summary>
        public int Count => _tokens.Length;

        /// <summary>
        /// Parses the arguments of a command message. The content is the full message as received
        /// (e.g. <c>!commandpermission hello 1</c>); the identifier+command token is stripped.
        /// </summary>
        public static CommandArguments Parse(string content)
        {
            string[] tokens = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new CommandArguments(content, tokens.Length > 1 ? tokens[1..] : Array.Empty<string>());
        }

        /// <summary>The raw argument at <paramref name="index"/>, or null when absent.</summary>
        public string? Get(int index)
        {
            return (uint)index < (uint)_tokens.Length ? _tokens[index] : null;
        }

        /// <summary>
        /// The arguments from <paramref name="index"/> to the end joined with single spaces, or null when
        /// the index is absent — the free-form text of commands like <c>quote &lt;text...&gt;</c>.
        /// </summary>
        public string? GetJoined(int index)
        {
            return (uint)index < (uint)_tokens.Length ? string.Join(' ', _tokens[index..]) : null;
        }

        /// <summary>
        /// The raw argument text from the first argument onward — the free-form text of commands
        /// whose content may span lines (e.g. the bio, FR-113). Unlike <see cref="GetJoined"/>,
        /// internal whitespace runs and line breaks are preserved verbatim; the text is trimmed
        /// at both ends. Null when the message has no arguments.
        /// </summary>
        public string? GetRaw()
        {
            int position = 0;
            while (position < _content.Length && char.IsWhiteSpace(_content[position]))
                position++;

            // Skip the identifier+command token and the separator run after it, whatever
            // whitespace it consists of — Split(' ') alone would eat newline separators.
            while (position < _content.Length && !char.IsWhiteSpace(_content[position]))
                position++;
            while (position < _content.Length && char.IsWhiteSpace(_content[position]))
                position++;

            string remainder = _content[position..].TrimEnd();
            return remainder.Length == 0 ? null : remainder;
        }

        /// <summary>Gets the required string argument at <paramref name="index"/>.</summary>
        public bool TryGetString(int index, out string value)
        {
            string? token = Get(index);
            value = token ?? string.Empty;
            return token != null;
        }

        /// <summary>Gets the argument at <paramref name="index"/> as an integer.</summary>
        public bool TryGetInt(int index, out int value)
        {
            value = 0;
            return int.TryParse(Get(index), out value);
        }

        /// <summary>
        /// Gets the argument at <paramref name="index"/> as a Discord user id, accepting raw ids
        /// and mentions (<c>&lt;@id&gt;</c> / <c>&lt;@!id&gt;</c>).
        /// </summary>
        public bool TryGetDiscordUserId(int index, out ulong discordId)
        {
            return DiscordUserId.TryParse(Get(index) ?? string.Empty, out discordId);
        }
    }
}
