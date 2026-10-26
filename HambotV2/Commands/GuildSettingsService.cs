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
        /// otherwise the global default. Results are cached per guild.
        /// </summary>
        string GetCommandIdentifier(ulong guildId);

        /// <summary>
        /// Writes the guild's command identifier override (<c>null</c> clears it back to the global
        /// default) and refreshes the cache, so subsequent messages in that guild use it immediately.
        /// The caller saves the provided context afterwards.
        /// </summary>
        void SetCommandIdentifier(ulong guildId, string? identifier, IDatabaseContext database);

        /// <summary>
        /// The effective tier the MCP operator agent acts at in the guild: its
        /// <c>GuildSetting.OperatorAgentTier</c> override if set, otherwise the lowest tier.
        /// Results are cached per guild.
        /// </summary>
        int GetOperatorAgentTier(ulong guildId);

        /// <summary>
        /// Writes the guild's operator-agent tier override (<c>null</c> clears it back to the
        /// lowest-tier default) and refreshes the cache. The caller saves the provided context
        /// afterwards.
        /// </summary>
        void SetOperatorAgentTier(ulong guildId, int? tier, IDatabaseContext database);

        /// <summary>
        /// Drops all cached per-guild identifiers, so the next read comes from the database — used
        /// after a backup restore replaced the data the cache was derived from.
        /// </summary>
        void InvalidateCache();
    }

    /// <summary>
    /// Guild-scoped settings resolution: guild overrides with global defaults.
    /// The service is a singleton so the resolved values are cached in memory — the per-message
    /// path (gateway pre-filter, command parsing, and the operator-agent tier check) never
    /// queries the database on a cache hit.
    /// </summary>
    internal class GuildSettingsService : IGuildSettingsService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<ulong, string> _identifierCache = new();
        private readonly ConcurrentDictionary<ulong, int> _operatorAgentTierCache = new();

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

        public int GetOperatorAgentTier(ulong guildId)
        {
            return _operatorAgentTierCache.GetOrAdd(guildId, guildId =>
            {
                using var scope = _serviceProvider.CreateScope();
                using var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
                return database.GuildSettings.FirstOrDefault(setting => setting.GuildId == guildId)?.OperatorAgentTier
                    ?? PermissionTier.Everyone;
            });
        }

        public void SetOperatorAgentTier(ulong guildId, int? tier, IDatabaseContext database)
        {
            var setting = database.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);
            if (setting != null)
            {
                setting.OperatorAgentTier = tier;
            }
            else
            {
                database.GuildSettings.Add(new DatabaseContext.GuildSetting
                {
                    GuildId = guildId,
                    OperatorAgentTier = tier
                });
            }

            _operatorAgentTierCache[guildId] = tier ?? PermissionTier.Everyone;
        }

        /// <summary>After a restore, the cached identifiers may no longer match the database.</summary>
        public void InvalidateCache()
        {
            _identifierCache.Clear();
            _operatorAgentTierCache.Clear();
        }
    }
}
