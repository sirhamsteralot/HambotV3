using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands.SystemCommands
{
    internal class HelloWorld : ICommand
    {
        public string Name => "hello";

        public HashSet<ulong> UserIdsList => new HashSet<ulong> { };
        public bool IsWhitelist => false;

        public async Task ExecuteCommand(SocketMessage socketMessage, ICommandContext context)
        {
            await socketMessage.Channel.SendMessageAsync("Hello World");
        }
    }
}
