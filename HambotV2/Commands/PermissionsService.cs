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
        public async Task<bool> CheckUserPermissions(ulong userDiscordId, TierOverrides overrides, ICommand command, IDatabaseContext database)
        {
            int commandTier = await GetCommandRequiredTier(command, database);
            int userTier = await GetUserEffectiveTier(userDiscordId, overrides, database);

            return CheckPermissions(userTier, commandTier);
        }

        public async Task<int> GetUserEffectiveTier(ulong userDiscordId, TierOverrides overrides, IDatabaseContext database)
        {
            var userMetaData = await database.Users.FirstOrDefaultAsync(x => x.DiscordId == userDiscordId);
            int storedTier = userMetaData?.PermissionTier ?? PermissionTier.Everyone;

            return overrides.EffectiveTierOf(userDiscordId, storedTier);
        }

        public async Task<int> GetCommandRequiredTier(ICommand command, IDatabaseContext database)
        {
            var commandMetaData = await database.Commands.FirstOrDefaultAsync(x => x.CommandName == command.Name);
            return commandMetaData?.PermissionTier ?? command.DefaultPermissionTier;
        }

        internal bool CheckPermissions(int userLevel, int requiredLevel)
        {
            return userLevel >= requiredLevel;
        }
    }
}
