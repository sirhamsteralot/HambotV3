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

        bool TryGetCommand(SocketUser author, string content, out ICommand command);
    }
}
