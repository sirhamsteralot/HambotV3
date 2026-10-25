using HambotV2.Connections;
using System.Collections.Concurrent;

namespace HambotV2.Commands
{
    internal interface IGuildSettingsService
    {
        /// <summary>The global default command identifier from configuration (used for direct messages).</summary>
        string GlobalCommandIdentifier { get; }

        /// <summary>
        /// The effective command identifier for a guild: its <c>GuildSetting</c> override if set,
        /// otherwise the global default (FR-26). Results are cached per guild.
        /// </summary>
        string GetCommandIdentifier(ulong guildId);

        /// <summary>
        /// Writes the guild's command identifier override (<c>null</c> clears it back to the global
        /// default) and refreshes the cache, so subsequent messages in that guild use it immediately.
        /// The caller saves the provided context afterwards.
        /// </summary>
        void SetCommandIdentifier(ulong guildId, string? identifier, IDatabaseContext database);

        /// <summary>
        /// Drops all cached per-guild identifiers, so the next read comes from the database — used
        /// after a backup restore replaced the data the cache was derived from (FR-98).
        /// </summary>
        void InvalidateCache();
    }

    /// <summary>
    /// Guild-scoped settings resolution (FR-26): guild overrides with global defaults.
    /// The service is a singleton so the resolved identifiers are cached in memory — the per-message
    /// path (gateway pre-filter and command parsing) never queries the database on a cache hit.
    /// </summary>
    internal class GuildSettingsService : IGuildSettingsService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<ulong, string> _identifierCache = new();

        public GuildSettingsService(IServiceProvider serviceProvider, IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            GlobalCommandIdentifier = configuration["BotConfiguration:CommandIdentifier"] ?? "!";
        }

        public string GlobalCommandIdentifier { get; }

        public string GetCommandIdentifier(ulong guildId)
        {
            return _identifierCache.GetOrAdd(guildId, guildId =>
            {
                // Cache miss: resolve a fresh transient context (this singleton never holds a
                // DbContext) and read the guild's override once.
                using var scope = _serviceProvider.CreateScope();
                using var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
                return database.GuildSettings.FirstOrDefault(setting => setting.GuildId == guildId)?.CommandIdentifier
                    ?? GlobalCommandIdentifier;
            });
        }

        public void SetCommandIdentifier(ulong guildId, string? identifier, IDatabaseContext database)
        {
            var setting = database.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);
            if (setting != null)
            {
                setting.CommandIdentifier = identifier;
            }
            else
            {
                database.GuildSettings.Add(new DatabaseContext.GuildSetting
                {
                    GuildId = guildId,
                    CommandIdentifier = identifier
                });
            }

            _identifierCache[guildId] = identifier ?? GlobalCommandIdentifier;
        }

        /// <summary>FR-98: after a restore, the cached identifiers may no longer match the database.</summary>
        public void InvalidateCache()
        {
            _identifierCache.Clear();
        }
    }
}
