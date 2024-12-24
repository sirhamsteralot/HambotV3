using HambotV2.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal interface IPermissionsService
    {
        public Task<bool> CheckUserPermissions(ulong userId, ICommand command, DatabaseContext database);
    }
}
