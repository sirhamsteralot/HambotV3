using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands.SystemCommands
{
    internal class RegisterSystemCommands
    {
        public static void RegisterCommands(ICommandService commandService)
        {
            commandService.RegisterCommand("hello", new HelloWorld());
        }
    }
}
