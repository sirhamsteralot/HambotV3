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
        Task<bool> CheckUserPermissions(ulong userDiscordId, TierOverrides overrides, ICommand command, IDatabaseContext database);

        /// <summary>The user's effective tier: stored database tier (0 if absent), raised by the bootstrap ids.</summary>
        Task<int> GetUserEffectiveTier(ulong userDiscordId, TierOverrides overrides, IDatabaseContext database);

        /// <summary>The command's required tier: database override if present, otherwise the command's built-in default.</summary>
        Task<int> GetCommandRequiredTier(ICommand command, IDatabaseContext database);
    }
}
