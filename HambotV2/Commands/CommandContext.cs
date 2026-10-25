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

        /// <summary>
        /// The guild the message was sent in (roadmap T14, FR-24); all state is scoped to it.
        /// Null in direct messages (FR-25): the DM scope applies — user at owner tier, command tiers
        /// at their built-in defaults, no per-guild state — and guild-state commands must refuse.
        /// </summary>
        public ulong? GuildId { get; private set; }

        /// <summary>True when the message arrived as a direct message (GuildId is null).</summary>
        public bool IsDirectMessage => GuildId is null;

        /// <summary>The effective command identifier: the guild's override or, in direct messages, the global default.</summary>
        public string CommandIdentifier { get; private set; }

        /// <summary>Guild-scoped settings resolution; commands use it to write guild settings with cache invalidation (roadmap T14).</summary>
        public IGuildSettingsService GuildSettings { get; private set; }

        public CommandContext(IDatabaseContext dbContext, ulong? guildId, string commandIdentifier, IGuildSettingsService guildSettings, TierOverrides? overrides = null)
        {
            DbContext = dbContext;
            GuildId = guildId;
            CommandIdentifier = commandIdentifier;
            GuildSettings = guildSettings;
            Overrides = overrides ?? TierOverrides.None;
        }

        public int GetUserEffectiveTier(ulong userDiscordId)
        {
            if (IsDirectMessage)
                return Overrides.EffectiveTierOf(userDiscordId, PermissionTier.Owner);

            int storedTier = DbContext.Users.FirstOrDefault(user => user.GuildId == GuildId && user.DiscordId == userDiscordId)?.PermissionTier
                ?? PermissionTier.Everyone;

            return Overrides.EffectiveTierOf(userDiscordId, storedTier);
        }
    }
}
