using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal interface ICommand
    {
        string Name { get; }
        public Task ExecuteCommand(SocketMessage socketMessage, ICommandContext context);
    }
}
