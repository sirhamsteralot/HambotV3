using HambotV2.Connections;

namespace HambotV2.Commands
{
    internal class CommandContext : ICommandContext
    {
        public IDatabaseContext DbContext { get; }

        public TierOverrides Overrides { get; }

        public ulong? GuildId { get; }

        public bool IsDirectMessage => GuildId is null;

        public string CommandIdentifier { get; }

        public IGuildSettingsService GuildSettings { get; }

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
            // In direct messages everyone acts at owner tier (FR-25); no database read happens.
            if (IsDirectMessage)
                return Overrides.EffectiveTierOf(userDiscordId, PermissionTier.Owner);

            int storedTier = DbContext.Users
                .FirstOrDefault(user => user.GuildId == GuildId && user.DiscordId == userDiscordId)?.PermissionTier
                ?? PermissionTier.Everyone;

            return Overrides.EffectiveTierOf(userDiscordId, storedTier);
        }
    }
}
