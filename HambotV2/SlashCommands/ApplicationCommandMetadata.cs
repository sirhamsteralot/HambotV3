namespace HambotV2.SlashCommands
{
    /// <summary>
    /// The input type of a typed application-command option (FR-110). Text and Integer map to
    /// their text-surface tokens verbatim; User and Channel map to the mention forms the
    /// pipeline's argument parsers already accept (<c>&lt;@id&gt;</c>, <c>&lt;#id&gt;</c>).
    /// </summary>
    internal enum ApplicationCommandOptionKind
    {
        /// <summary>A free-text string option (maps to <see cref="Discord.ApplicationCommandOptionType.String"/>).</summary>
        Text,

        /// <summary>An integer option (maps to <see cref="Discord.ApplicationCommandOptionType.Integer"/>).</summary>
        Integer,

        /// <summary>A user picker option (maps to <see cref="Discord.ApplicationCommandOptionType.User"/>).</summary>
        User,

        /// <summary>A channel picker option (maps to <see cref="Discord.ApplicationCommandOptionType.Channel"/>).</summary>
        Channel,
    }

    /// <summary>
    /// One typed application-command option (FR-110): the name is the slash option's name, the
    /// value maps to the command's equivalent text-surface token.
    /// </summary>
    /// <param name="Name">The option name shown in Discord's UI (lowercase, 1–32 characters).</param>
    /// <param name="Description">The option description shown in Discord's UI (1–100 characters).</param>
    /// <param name="Kind">The option's input type and text-surface mapping.</param>
    /// <param name="Required">True when Discord requires the option before invoking.</param>
    internal sealed record ApplicationCommandOption(string Name, string Description, ApplicationCommandOptionKind Kind, bool Required = true)
    {
        /// <summary>The name of the single free-form option commands without typed metadata register (FR-110).</summary>
        internal const string FreeFormOptionName = "arguments";

        /// <summary>
        /// The free-form fallback option: the whole argument line exactly as it would follow the
        /// text-surface command name, so the invocation maps to the equivalent <c>!&lt;name&gt; &lt;arguments&gt;</c> line.
        /// </summary>
        public static ApplicationCommandOption FreeForm(string description)
        {
            return new ApplicationCommandOption(FreeFormOptionName, description, ApplicationCommandOptionKind.Text, Required: false);
        }
    }

    /// <summary>
    /// A command's slash-surface declaration (FR-110). An instance on a command opts it into the
    /// slash surface; the option list tells the registration pass what to build and the
    /// interaction handler how to map an invocation back to its text command line:
    /// typed options join in declaration order, and the single free-form option (if declared)
    /// becomes the whole argument line.
    /// </summary>
    internal sealed class ApplicationCommandDeclaration
    {
        /// <summary>For commands that take no arguments (e.g. backup, health): no options at all.</summary>
        public static ApplicationCommandDeclaration NoArguments { get; } = new([]);

        /// <summary>For commands whose invocation stays a free-form argument line (the FR-110 fallback).</summary>
        public static ApplicationCommandDeclaration FreeForm(string optionDescription)
        {
            return new([ApplicationCommandOption.FreeForm(optionDescription)]);
        }

        public ApplicationCommandDeclaration(IReadOnlyList<ApplicationCommandOption> options)
        {
            Options = options;
        }

        /// <summary>The ordered typed options; Discord requires optional options to come after required ones.</summary>
        public IReadOnlyList<ApplicationCommandOption> Options { get; }
    }
}
