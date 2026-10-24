using Discord;
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
        public string Description => "Greets the channel";
        public string Usage => "hello";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        public async Task ExecuteCommand(IMessage socketMessage, ICommandContext context)
        {
            await socketMessage.Channel.SendMessageAsync(GetResponse());
        }

        internal string GetResponse()
        {
            return "Hello World";
        }
    }
}
