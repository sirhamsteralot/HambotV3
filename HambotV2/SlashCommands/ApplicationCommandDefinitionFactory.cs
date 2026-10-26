using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HambotV2.Commands;

namespace HambotV2.SlashCommands
{
    /// <summary>The application-command definition pushed to Discord — bot-owned DTO, kept free of Discord.Net types so the registration contract is unit-testable (NFR-26).</summary>
    /// <param name="Name">The command name; shared with the text-surface registry key.</param>
    /// <param name="Description">The description shown in Discord's command picker; from the command's help text (FR-39).</param>
    /// <param name="Options">The ordered options (typed, or the single free-form fallback).</param>
    internal sealed record ApplicationCommandDefinition(string Name, string Description, IReadOnlyList<ApplicationCommandOption> Options);

    /// <summary>
    /// Builds one application-command definition per slash-eligible command from the same
    /// registry entries as the text surface (FR-110): name and description from <see cref="ICommand"/>,
    /// options from the command's <see cref="ICommand.ApplicationCommand"/> declaration.
    /// Also the single chokepoint for Discord's registration limits (name, description, and
    /// option lengths), so an invalid command fails loudly at registration time, never silently.
    /// </summary>
    internal static class ApplicationCommandDefinitionFactory
    {
        /// <summary>Discord's command/option name rule (name characters, 1–32).</summary>
        private static readonly Regex NamePattern = new("^[-_\\p{L}\\p{N}]{1,32}$", RegexOptions.Compiled);

        private const int MaxDescriptionLength = 100;
        private const int MaxOptions = 25;

        /// <summary>
        /// Builds the definition for a slash-eligible command (one whose
        /// <see cref="ICommand.ApplicationCommand"/> is non-null; the caller skips the rest).
        /// Returns false — with the rule it broke — when the declaration violates one of
        /// Discord's registration limits, so an invalid command fails loudly at registration time.
        /// </summary>
        public static bool TryCreate(ICommand command, [NotNullWhen(true)] out ApplicationCommandDefinition? definition, [NotNullWhen(false)] out string? error)
        {
            definition = null;

            if (command.ApplicationCommand is not { } declaration)
            {
                error = $"Command '{command.Name}' has no application-command declaration (check the eligibility guard before building definitions).";
                return false;
            }

            if (!NamePattern.IsMatch(command.Name))
            {
                error = $"Command '{command.Name}' has an invalid application-command name.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(command.Description) || command.Description.Trim().Length > MaxDescriptionLength)
            {
                error = $"Command '{command.Name}' needs a non-empty description of at most {MaxDescriptionLength} characters for the application-command surface.";
                return false;
            }

            if (declaration.Options.Count > MaxOptions)
            {
                error = $"Command '{command.Name}' declares more than {MaxOptions} application-command options.";
                return false;
            }

            // Discord requires optional options to come after required ones; the declaration order
            // is also the order the invocation maps back onto the text command line.
            bool seenOptional = false;
            foreach (ApplicationCommandOption option in declaration.Options)
            {
                if (!NamePattern.IsMatch(option.Name) || option.Name != option.Name.ToLowerInvariant())
                {
                    error = $"Command '{command.Name}' declares application-command option '{option.Name}', which is not a lowercase 1–32 character name.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(option.Description) || option.Description.Trim().Length > MaxDescriptionLength)
                {
                    error = $"Command '{command.Name}' declares application-command option '{option.Name}' without a description of at most {MaxDescriptionLength} characters.";
                    return false;
                }

                if (option.Required && seenOptional)
                {
                    error = $"Command '{command.Name}' must declare required application-command options before optional ones ('{option.Name}').";
                    return false;
                }

                if (!option.Required)
                    seenOptional = true;
            }

            definition = new ApplicationCommandDefinition(command.Name, command.Description.Trim(), declaration.Options);
            error = null;
            return true;
        }
    }
}
