using Discord;

namespace HambotV2.Connections
{
    /// <summary>
    /// The slice of the gateway client the presence service needs — a seam so the service is
    /// unit-testable against a mock. Production implementation:
    /// <see cref="DiscordPresenceGateway"/>.
    /// </summary>
    internal interface IPresenceGateway
    {
        /// <summary>Raised once the gateway is ready (guilds downloaded); the service flushes deferred requests.</summary>
        event Func<Task> Ready;

        bool IsReady { get; }

        /// <summary>A null text clears the activity.</summary>
        Task SetActivityAsync(ActivityType type, string? text);

        IReadOnlyCollection<ulong> GetGuildIds();

        /// <summary>A null value clears the field on the Discord side.</summary>
        Task SetGuildBioAsync(ulong guildId, string? bio);

        Task SetGuildAvatarAsync(ulong guildId, byte[]? imageData);

        Task SetGuildBannerAsync(ulong guildId, byte[]? imageData);

        Task SetGlobalAvatarAsync(byte[]? imageData);

        string? GetAvatarUrl();

        string? GetBannerUrl();
    }
}
