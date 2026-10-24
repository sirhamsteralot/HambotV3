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
        public IDatabaseContext DbContext { get; private set; }

        public TierOverrides Overrides { get; private set; }

        public CommandContext(IDatabaseContext dbContext, TierOverrides? overrides = null)
        {
            DbContext = dbContext;
            Overrides = overrides ?? TierOverrides.None;
        }

        public int GetUserEffectiveTier(ulong userDiscordId)
        {
            int storedTier = DbContext.Users.FirstOrDefault(user => user.DiscordId == userDiscordId)?.PermissionTier
                ?? PermissionTier.Everyone;

            return Overrides.EffectiveTierOf(userDiscordId, storedTier);
        }
    }
}
