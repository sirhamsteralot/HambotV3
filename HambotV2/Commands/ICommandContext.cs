using HambotV2.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal interface ICommandContext
    {
        IDatabaseContext DbContext { get; }

        /// <summary>The tier overrides applying to the current message (guild owner, configured admin, bot owner).</summary>
        TierOverrides Overrides { get; }

        /// <summary>
        /// The user's effective permission tier: the stored database tier, raised by the tier overrides
        /// (guild owner / configured admin at owner tier, bot owner at bot owner tier).
        /// </summary>
        int GetUserEffectiveTier(ulong userDiscordId);
    }
}
