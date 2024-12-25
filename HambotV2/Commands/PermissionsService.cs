using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal class PermissionsService : IPermissionsService
    {
        public async Task<bool> CheckUserPermissions(ulong userDiscordId, ICommand command, DatabaseContext database)
        {
            var commandMetaData = await database.Commands.FirstOrDefaultAsync(x => x.CommandName == command.Name);
            int commandTier = 0;

            if (commandMetaData != null)
                commandTier = commandMetaData.PermissionTier;

            var userMetaData = await database.Users.FirstOrDefaultAsync(x => x.DiscordId == userDiscordId);
            int userTier = 0;

            if (userMetaData != null)
                userTier = userMetaData.PermissionTier;

            return CheckPermissions(userTier, commandTier);
        }

        internal bool CheckPermissions(int userLevel, int requiredLevel)
        {
            return userLevel >= requiredLevel;
        }
    }
}
