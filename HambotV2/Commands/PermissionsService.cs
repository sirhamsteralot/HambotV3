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
        public async Task<bool> CheckUserPermissions(ulong userDiscordId, ulong? guildId, TierOverrides overrides, ICommand command, IDatabaseContext database)
        {
            int commandTier = await GetCommandRequiredTier(guildId, command, database);
            int userTier = await GetUserEffectiveTier(userDiscordId, guildId, overrides, database);

            return CheckPermissions(userTier, commandTier);
        }

        public Task<int> GetUserEffectiveTier(ulong userDiscordId, ulong? guildId, TierOverrides overrides, IDatabaseContext database)
        {
            if (guildId is null)
            {
                // Direct-message scope (FR-25): everyone acts at owner tier, the configured bot
                // owner at bot owner tier — no per-guild state is read. The stored tier fed into the
                // override computation is owner, so the bot-owner raise is the only possible change.
                return Task.FromResult(overrides.EffectiveTierOf(userDiscordId, PermissionTier.Owner));
            }

            return GetUserEffectiveTier(userDiscordId, guildId.Value, overrides, database);
        }

        private static async Task<int> GetUserEffectiveTier(ulong userDiscordId, ulong guildId, TierOverrides overrides, IDatabaseContext database)
        {
            var userMetaData = await database.Users.FirstOrDefaultAsync(x => x.GuildId == guildId && x.DiscordId == userDiscordId);
            int storedTier = userMetaData?.PermissionTier ?? PermissionTier.Everyone;

            return overrides.EffectiveTierOf(userDiscordId, storedTier);
        }

        public Task<int> GetCommandRequiredTier(ulong? guildId, ICommand command, IDatabaseContext database)
        {
            if (guildId is null)
                return Task.FromResult(command.DefaultPermissionTier);

            return GetCommandRequiredTier(guildId.Value, command, database);
        }

        private static async Task<int> GetCommandRequiredTier(ulong guildId, ICommand command, IDatabaseContext database)
        {
            var commandMetaData = await database.Commands.FirstOrDefaultAsync(x => x.GuildId == guildId && x.CommandName == command.Name);
            return commandMetaData?.PermissionTier ?? command.DefaultPermissionTier;
        }

        internal bool CheckPermissions(int userLevel, int requiredLevel)
        {
            return userLevel >= requiredLevel;
        }
    }
}
