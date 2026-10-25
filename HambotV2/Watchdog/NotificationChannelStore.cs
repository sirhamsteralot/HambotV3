using HambotV2.Connections;

namespace HambotV2.Watchdog
{
    /// <summary>
    /// Resolves the operator notification channel (FR-117): the runtime `BotSetting` override
    /// persisted by `!notifychannel`, with the optional configuration key
    /// `Notifications:ChannelId` as the default when no override exists. Reading and writing go
    /// through fresh scopes, so this singleton never holds a DbContext.
    /// </summary>
    internal interface INotificationChannelStore
    {
        /// <summary>The effective channel id, or null when notifications degrade to logs and owner DMs (FR-118).</summary>
        Task<ulong?> GetChannelIdAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists the runtime channel override across restarts; null clears it, so the
        /// configuration default (if any) applies again.
        /// </summary>
        Task SetChannelIdAsync(ulong? channelId, CancellationToken cancellationToken = default);
    }

    internal sealed class NotificationChannelStore : INotificationChannelStore
    {
        /// <summary>The `BotSetting` key holding the runtime notification channel override (FR-117).</summary>
        internal const string ChannelSettingKey = "NotificationChannelId";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;

        public NotificationChannelStore(IServiceScopeFactory scopeFactory, IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
        }

        public async Task<ulong?> GetChannelIdAsync(CancellationToken cancellationToken = default)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            string? stored = (await database.BotSettings.FindAsync(new object[] { ChannelSettingKey }, cancellationToken))?.Value;
            if (!string.IsNullOrEmpty(stored) && ulong.TryParse(stored, out ulong runtimeChannel))
                return runtimeChannel;

            // The configuration default applies when no runtime override exists (FR-117).
            return ulong.TryParse(_configuration["Notifications:ChannelId"], out ulong configuredChannel)
                ? configuredChannel
                : null;
        }

        public async Task SetChannelIdAsync(ulong? channelId, CancellationToken cancellationToken = default)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            var setting = await database.BotSettings.FindAsync(new object[] { ChannelSettingKey }, cancellationToken);
            if (channelId is null)
            {
                // "clear": the stored override is removed, so the configuration default (if any)
                // applies again — nothing is persisted that would shadow it (FR-117).
                if (setting is not null)
                    database.BotSettings.Remove(setting);
            }
            else if (setting is null)
            {
                database.BotSettings.Add(new DatabaseContext.BotSetting
                {
                    Key = ChannelSettingKey,
                    Value = channelId.Value.ToString()
                });
            }
            else
            {
                setting.Value = channelId.Value.ToString();
            }

            await database.SaveChangesAsync(cancellationToken);
        }
    }
}
