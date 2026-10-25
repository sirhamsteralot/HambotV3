using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands
{
    internal class PermissionsService : IPermissionsService
    {
        public Task<PermissionCheckResult> CheckUserPermissions(ulong userDiscordId, ulong? guildId, TierOverrides overrides, ICommand command, IDatabaseContext database)
        {
            return CheckUserPermissions(userDiscordId, guildId, overrides, command.Name, command.DefaultPermissionTier, database);
        }

        public async Task<PermissionCheckResult> CheckUserPermissions(ulong userDiscordId, ulong? guildId, TierOverrides overrides, string commandName, int defaultPermissionTier, IDatabaseContext database)
        {
            int requiredTier = await GetCommandRequiredTier(guildId, commandName, defaultPermissionTier, database);
            int userTier = await GetUserEffectiveTier(userDiscordId, guildId, overrides, database);

            return new PermissionCheckResult(userTier >= requiredTier, userTier);
        }

        public Task<int> GetUserEffectiveTier(ulong userDiscordId, ulong? guildId, TierOverrides overrides, IDatabaseContext database)
        {
            if (guildId is null)
            {
                // DM scope (FR-25): everyone acts at owner tier, the configured bot owner at bot
                // owner tier — no per-guild state is read.
                return Task.FromResult(overrides.EffectiveTierOf(userDiscordId, PermissionTier.Owner));
            }

            return GetUserEffectiveTier(userDiscordId, guildId.Value, overrides, database);
        }

        private static async Task<int> GetUserEffectiveTier(ulong userDiscordId, ulong guildId, TierOverrides overrides, IDatabaseContext database)
        {
            var user = await database.Users.FirstOrDefaultAsync(x => x.GuildId == guildId && x.DiscordId == userDiscordId);
            int storedTier = user?.PermissionTier ?? PermissionTier.Everyone;

            return overrides.EffectiveTierOf(userDiscordId, storedTier);
        }

        public Task<int> GetCommandRequiredTier(ulong? guildId, ICommand command, IDatabaseContext database)
        {
            return GetCommandRequiredTier(guildId, command.Name, command.DefaultPermissionTier, database);
        }

        private static Task<int> GetCommandRequiredTier(ulong? guildId, string commandName, int defaultPermissionTier, IDatabaseContext database)
        {
            if (guildId is null)
                return Task.FromResult(defaultPermissionTier);

            return GetCommandRequiredTier(guildId.Value, commandName, defaultPermissionTier, database);
        }

        private static async Task<int> GetCommandRequiredTier(ulong guildId, string commandName, int defaultPermissionTier, IDatabaseContext database)
        {
            var commandMetaData = await database.Commands.FirstOrDefaultAsync(x => x.GuildId == guildId && x.CommandName == commandName);
            return commandMetaData?.PermissionTier ?? defaultPermissionTier;
        }
    }
}
