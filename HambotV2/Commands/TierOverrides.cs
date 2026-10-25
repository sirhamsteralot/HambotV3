namespace HambotV2.Commands
{
    /// <summary>
    /// Tier overrides: Discord ids whose permission tier is implicit instead of stored in the
    /// database (FR-34/FR-41). The guild owner acts at owner tier in their guild;
    /// the configured bot owner acts at bot owner tier in every guild. Bundled into one value so
    /// permission checks don't grow an id parameter per override source.
    /// </summary>
    internal sealed record TierOverrides
    {
        public static TierOverrides None { get; } = new();

        /// <summary>Discord id of the user owning the guild the message came from; null in direct messages.</summary>
        public ulong? GuildOwnerId { get; init; }

        /// <summary>Discord id configured under Permissions:BotOwnerDiscordId; null when unset.</summary>
        public ulong? BotOwnerId { get; init; }

        /// <summary>The user's effective tier: the stored database tier, raised to owner for the guild owner and to bot owner for the configured bot owner.</summary>
        public int EffectiveTierOf(ulong userDiscordId, int storedTier)
        {
            if (userDiscordId == BotOwnerId)
                return PermissionTier.BotOwner;

            if (userDiscordId == GuildOwnerId)
                return PermissionTier.Owner;

            return storedTier;
        }

        /// <summary>
        /// A short label for users whose effective tier comes from these overrides rather than the
        /// database ("bot owner" / "guild owner"); null when the stored tier applies unchanged.
        /// </summary>
        public string? DescribeOverride(ulong userDiscordId, int storedTier)
        {
            if (userDiscordId == BotOwnerId)
                return storedTier == PermissionTier.BotOwner ? null : "bot owner";

            if (userDiscordId == GuildOwnerId)
                return storedTier >= PermissionTier.Owner ? null : "guild owner";

            return null;
        }
    }
}
