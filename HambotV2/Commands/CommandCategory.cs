namespace HambotV2.Commands
{
    /// <summary>
    /// The group a command is listed under in the help list. The declaration order is the
    /// groups' display order. Presentation-only: a category never affects permissions,
    /// cooldowns, or execution.
    /// </summary>
    internal enum CommandCategory
    {
        General,
        Quotes,
        CustomResponses,
        Reminders,
        PermissionsAndSettings,
        BackupsAndStability,
        BotProfile
    }

    /// <summary>The validity check and the help list's group header for each category.</summary>
    internal static class CommandCategories
    {
        public const CommandCategory Minimum = CommandCategory.General;
        public const CommandCategory Maximum = CommandCategory.BotProfile;

        public static bool IsValid(CommandCategory category) => category >= Minimum && category <= Maximum;

        public static string Header(CommandCategory category) => category switch
        {
            CommandCategory.General => "General",
            CommandCategory.Quotes => "Quotes",
            CommandCategory.CustomResponses => "Custom responses",
            CommandCategory.Reminders => "Reminders",
            CommandCategory.PermissionsAndSettings => "Permissions & settings",
            CommandCategory.BackupsAndStability => "Backups & stability",
            CommandCategory.BotProfile => "Bot profile",
            _ => "Unknown"
        };
    }
}
