using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands
{
    /// <summary>
    /// The "did you mean?" consumer of the shared fuzzy matcher: builds the candidate names
    /// the invoker could actually run — every built-in, plus in a guild the guild's custom
    /// response names — filters them by tier before matching so a suggestion never reveals a
    /// command above the invoker's tier, and returns the single best match.
    /// </summary>
    internal static class CommandSuggestions
    {
        /// <summary>The closeness rule: edit distance ≤ 2, or a prefix hit for a token of ≥ 3 characters.</summary>
        private const int MaxDistance = 2;
        private const int MinPrefixLength = 3;

        public static async Task<string?> FindBestAsync(
            ulong? guildId,
            ulong userId,
            TierOverrides overrides,
            string token,
            ICommandService commandService,
            IPermissionsService permissionsService,
            IDatabaseContext database)
        {
            // Tier filtering before matching, with the same data the dispatch path uses: the
            // invoker's effective tier (DM scope: owner tier; virtual invocations: their
            // operator-agent grant) and each candidate's required tier from the per-guild
            // CommandMetaData overrides.
            int userTier = await permissionsService.GetUserEffectiveTier(userId, guildId, overrides, database);

            var requiredTiers = new Dictionary<string, int>();
            if (guildId is ulong guild)
            {
                requiredTiers = await database.Commands
                    .Where(metadata => metadata.GuildId == guild)
                    .ToDictionaryAsync(metadata => metadata.CommandName, metadata => metadata.PermissionTier);
            }

            var candidates = new List<string>();
            foreach (ICommand command in commandService.Commands.Values)
            {
                if (userTier >= RequiredTier(command.Name, command.DefaultPermissionTier))
                    candidates.Add(command.Name);
            }

            if (guildId is ulong responseGuild)
            {
                List<string> responseNames = await database.CustomResponses
                    .Where(response => response.GuildId == responseGuild)
                    .Select(response => response.Name)
                    .ToListAsync();
                candidates.AddRange(responseNames.Where(name => userTier >= RequiredTier(name, PermissionTier.Everyone)));
            }

            return FuzzyMatcher.Best(token, candidates, (candidate, distance) =>
                distance <= MaxDistance
                || (token.Length >= MinPrefixLength && candidate.StartsWith(token, StringComparison.Ordinal)));

            int RequiredTier(string name, int defaultTier)
                => requiredTiers.TryGetValue(name, out int tier) ? tier : defaultTier;
        }
    }
}
