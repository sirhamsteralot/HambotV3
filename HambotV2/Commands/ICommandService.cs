using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal interface ICommandService
    {
        Dictionary<string, ICommand> Commands { get; }
        bool CommandsRegistered { get; internal set; }

        bool TryGetCommand(string content, out ICommand command);
        void RegisterCommand(string commandName, ICommand command);
        void RegisterCommands();
    }
}
