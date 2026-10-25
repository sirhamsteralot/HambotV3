using HambotV2.Connections;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace HambotV2.Commands
{
    /// <summary>
    /// Per-(guild, user, command) in-memory command cooldown (NFR-12). Singleton: the
    /// recorded timestamps are shared across messages while the rest of the handler chain stays
    /// transient. The cooldown window is resolved per guild from the invoking user's effective tier:
    /// the guild's <c>GuildSetting</c> per-tier override if set, otherwise the global per-tier
    /// default from <c>BotConfiguration:CooldownSecondsByTier</c> (3 seconds when a tier has no
    /// entry). A window of 0 seconds disables throttling for that tier in that scope.
    ///
    /// Tier 4 (bot owner) is never bound by a guild override — it is a global, configuration-only
    /// role (FR-41), so a single guild's settings must not govern it. Direct messages run in the DM
    /// scope (FR-25): their key uses the sentinel guild id 0, which matches no real guild, and no
    /// guild state is read, so DM throttles resolve through the tier-3 window.
    /// </summary>
    internal sealed class CooldownService : ICooldownService
    {
        /// <summary>Sentinel guild id for direct-message throttle keys; no real guild has id 0.</summary>
        public const ulong DirectMessageGuildId = 0;

        /// <summary>Window applied to a tier that has no global configuration entry.</summary>
        private const int DefaultWindowSeconds = 3;

        /// <summary>Highest tier configurable per guild; tier 4 (bot owner) is global, configuration-only.</summary>
        private const int HighestGuildConfigurableTier = PermissionTier.Owner;

        private readonly ConcurrentDictionary<CooldownKey, DateTimeOffset> _lastInvocationUtc = new();
        private readonly int?[] _globalWindowSeconds = new int?[PermissionTier.Maximum + 1];
        private readonly TimeProvider _timeProvider;

        public CooldownService(TimeProvider timeProvider, IConfiguration configuration)
        {
            _timeProvider = timeProvider;

            for (int tier = PermissionTier.Minimum; tier <= PermissionTier.Maximum; tier++)
            {
                // Invalid or negative entries are treated as absent, so a typo in configuration
                // falls back to the default window instead of breaking command handling.
                if (int.TryParse(configuration[$"BotConfiguration:CooldownSecondsByTier:{tier}"], out int seconds) && seconds >= 0)
                    _globalWindowSeconds[tier] = seconds;
            }
        }

        public CooldownCheck TryBeginInvocation(ulong? guildId, ulong userId, string commandName, int userEffectiveTier, IDatabaseContext database)
        {
            int windowSeconds = ResolveWindowSeconds(guildId, userEffectiveTier, database);
            if (windowSeconds <= 0)
                return CooldownCheck.Allowed;

            var key = new CooldownKey(guildId ?? DirectMessageGuildId, userId, commandName);
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (_lastInvocationUtc.TryGetValue(key, out DateTimeOffset lastInvocation))
            {
                DateTimeOffset windowEnd = lastInvocation + TimeSpan.FromSeconds(windowSeconds);
                if (now < windowEnd)
                    return new CooldownCheck(IsAllowed: false, windowEnd - now);

                // Expired entries are removed opportunistically when their key is next checked, so
                // the dictionary stays bounded by the distinct (guild, user, command) triples that
                // were recently used.
                _lastInvocationUtc.TryRemove(key, out _);
            }

            _lastInvocationUtc[key] = now;
            return CooldownCheck.Allowed;
        }

        private int ResolveWindowSeconds(ulong? guildId, int userEffectiveTier, IDatabaseContext database)
        {
            if (userEffectiveTier >= PermissionTier.Minimum && userEffectiveTier <= HighestGuildConfigurableTier && guildId is ulong guild)
            {
                var setting = database.GuildSettings.FirstOrDefault(item => item.GuildId == guild);
                if (setting?.GetCooldownOverride(userEffectiveTier) is int guildOverride)
                    return guildOverride;
            }

            return GlobalWindowSeconds(userEffectiveTier);
        }

        private int GlobalWindowSeconds(int tier)
        {
            return tier >= PermissionTier.Minimum && tier <= PermissionTier.Maximum
                ? _globalWindowSeconds[tier] ?? DefaultWindowSeconds
                : DefaultWindowSeconds;
        }

        private readonly record struct CooldownKey(ulong GuildId, ulong UserId, string CommandName);
    }
}
