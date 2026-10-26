using Discord;
using HambotV2.SlashCommands;

namespace HambotV2.Commands
{
    internal interface ICommand
    {
        /// <summary>Registry key; must be lowercase (lookup lowercases input).</summary>
        string Name { get; }

        /// <summary>One-line summary shown in the !help list.</summary>
        string Description { get; }

        /// <summary>Argument summary without the command identifier, e.g. <c>userpermission &lt;user&gt; [tier]</c>.</summary>
        string Usage { get; }

        /// <summary>Required permission tier when no CommandMetaData override exists in the database.</summary>
        int DefaultPermissionTier { get; }

        /// <summary>
        /// Slash-surface declaration: null — the default — keeps the command text-only;
        /// an instance opts the command into the Discord application-command surface. Only
        /// administrative/configuration commands and commands acting solely on the invoking user's
        /// own state declare one.
        /// </summary>
        ApplicationCommandDeclaration? ApplicationCommand => null;

        Task ExecuteCommand(IMessage message, ICommandContext context);
    }
}
