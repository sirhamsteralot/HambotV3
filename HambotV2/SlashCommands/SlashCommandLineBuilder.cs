using System.Globalization;

namespace HambotV2.SlashCommands
{
    /// <summary>
    /// Maps a slash invocation onto its equivalent text command line: the values
    /// of typed options join in declaration order, mapped to the text-surface tokens the
    /// pipeline's argument parsers accept; the free-form fallback option becomes the whole
    /// argument line. Pure and Discord-free, so the mapping is unit-testable.
    /// </summary>
    internal static class SlashCommandLineBuilder
    {
        /// <summary>
        /// Builds <c>name arg1 arg2 ...</c> from the invocation's option values (keyed by option
        /// name, as the interaction surface reports them). Returns false when a value cannot be
        /// mapped — the caller answers with the generic error, nothing executes.
        /// </summary>
        public static bool TryBuild(string commandName, ApplicationCommandDeclaration declaration, IReadOnlyDictionary<string, object?> optionValues, out string commandLine)
        {
            var tokens = new List<string>();

            foreach (ApplicationCommandOption option in declaration.Options)
            {
                if (!optionValues.TryGetValue(option.Name, out object? value) || value is null)
                    continue;

                if (!TryMapValue(option, value, out string? token) || token is null)
                {
                    commandLine = string.Empty;
                    return false;
                }

                tokens.Add(token);
            }

            commandLine = tokens.Count == 0 ? commandName : $"{commandName} {string.Join(' ', tokens)}";
            return true;
        }

        private static bool TryMapValue(ApplicationCommandOption option, object value, out string? token)
        {
            token = option.Kind switch
            {
                // Text passes through as its own token; integers render invariantly.
                ApplicationCommandOptionKind.Text => value.ToString(),
                ApplicationCommandOptionKind.Integer => Convert.ToString(value, CultureInfo.InvariantCulture),

                // Pickers report snowflakes: render the mention forms the text-surface parsers
                // accept (<@id> for users, <#id> for channels).
                ApplicationCommandOptionKind.User => FormattedMention(value, prefix: "<@", suffix: ">"),
                ApplicationCommandOptionKind.Channel => FormattedMention(value, prefix: "<#", suffix: ">"),
                _ => null,
            };

            return token is not null;
        }

        private static string? FormattedMention(object value, string prefix, string suffix)
        {
            if (value is not ulong and not long)
                return null;

            try
            {
                return $"{prefix}{Convert.ToUInt64(value, CultureInfo.InvariantCulture)}{suffix}";
            }
            catch (OverflowException)
            {
                return null;
            }
        }
    }
}
