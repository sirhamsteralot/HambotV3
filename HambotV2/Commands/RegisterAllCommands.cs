using HambotV2.Commands.SystemCommands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal class RegisterAllCommands
    {
        public static void RegisterCommands(ICommandService commandService)
        {
            RegisterSystemCommands.RegisterCommands(commandService);
        }
    }
}
