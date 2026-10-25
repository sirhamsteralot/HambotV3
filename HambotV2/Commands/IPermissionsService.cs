using HambotV2.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal interface IPermissionsService
    {
        /// <summary>
        /// Checks the invocation in the given scope. A null <paramref name="guildId"/> means a direct
        /// message (FR-25): the user acts at owner tier (bot owner at tier 4), commands require their
        /// built-in defaults, and no database state is read or written.
        /// </summary>
        Task<bool> CheckUserPermissions(ulong userDiscordId, ulong? guildId, TierOverrides overrides, ICommand command, IDatabaseContext database);

        /// <summary>The user's effective tier in the guild: stored database tier (0 if absent), raised by the bootstrap ids. Null guild: the DM scope tier (FR-25), without a database read.</summary>
        Task<int> GetUserEffectiveTier(ulong userDiscordId, ulong? guildId, TierOverrides overrides, IDatabaseContext database);

        /// <summary>The command's required tier in the guild: database override if present, otherwise the command's built-in default. Null guild: always the built-in default (FR-25), without a database read.</summary>
        Task<int> GetCommandRequiredTier(ulong? guildId, ICommand command, IDatabaseContext database);
    }
}
