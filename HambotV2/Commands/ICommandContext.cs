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
        /// The guild the message was sent in (roadmap T14, FR-24); all persisted state is scoped to it.
        /// Null in direct messages (FR-25): the DM scope applies (owner-tier user, built-in default
        /// command tiers) and guild-state commands must refuse.
        /// </summary>
        ulong? GuildId { get; }

        /// <summary>True when the message arrived as a direct message (GuildId is null).</summary>
        bool IsDirectMessage { get; }

        /// <summary>The effective command identifier: the guild's override or, in direct messages, the global default (FR-26).</summary>
        string CommandIdentifier { get; }

        /// <summary>Guild-scoped settings resolution; commands use it to write guild settings with cache invalidation (roadmap T14).</summary>
        IGuildSettingsService GuildSettings { get; }

        /// <summary>
        /// The user's effective permission tier in this guild: the stored database tier, raised by the
        /// tier overrides (guild owner at owner tier, bot owner at bot owner tier).
        /// </summary>
        int GetUserEffectiveTier(ulong userDiscordId);
    }
}
