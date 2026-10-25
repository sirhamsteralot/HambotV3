using Discord.WebSocket;
using HambotV2.Connections;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal interface ICommandService
    {
        Dictionary<string, ICommand> Commands { get; }
        bool CommandsRegistered { get; internal set; }

        /// <summary>True when the message is the command identifier (plus whitespace) only; such messages are ignored silently.</summary>
        bool IsIdentifierOnlyMessage(string content, string identifier);

        bool TryGetCommand(string content, string identifier, [NotNullWhen(true)] out ICommand? command);
        void RegisterCommand(string commandName, ICommand command);
        void RegisterCommands();
    }
}
