using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal interface ICommand
    {
        /// <summary>Registry key; must be lowercase (lookup lowercases input).</summary>
        string Name { get; }

        /// <summary>One-line summary shown in the !help list (FR-39).</summary>
        string Description { get; }

        /// <summary>Argument summary without the command identifier, e.g. <c>userpermission &lt;user&gt; [tier]</c>.</summary>
        string Usage { get; }

        /// <summary>Required permission tier when no CommandMetaData override exists in the database (FR-35).</summary>
        int DefaultPermissionTier { get; }

        Task ExecuteCommand(IMessage socketMessage, ICommandContext context);
    }
}
