using HambotV2.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal class CommandContext : ICommandContext
    {
        public DatabaseContext DbContext { get; private set; }

        public CommandContext(DatabaseContext dbContext) 
        {
            DbContext = dbContext;
        }

    }
}
