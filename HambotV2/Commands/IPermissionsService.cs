using HambotV2.Connections;

namespace HambotV2.Commands
{
    /// <summary>
    /// Outcome of the permission check: the verdict plus the user's effective tier it was decided
    /// with. The tier is carried along so the pipeline's cooldown check can resolve the invoking
    /// user's cooldown window without repeating the database lookup.
    /// </summary>
    internal readonly record struct PermissionCheckResult(bool IsAllowed, int UserEffectiveTier);

    internal interface IPermissionsService
    {
        /// <summary>
        /// Checks the invocation in the given scope. A null <paramref name="guildId"/> means a direct
        /// message: the user acts at owner tier (bot owner at tier 4), commands require their
        /// built-in defaults, and no database state is read or written.
        /// </summary>
        Task<PermissionCheckResult> CheckUserPermissions(ulong userDiscordId, ulong? guildId, TierOverrides overrides, ICommand command, IDatabaseContext database);

        /// <summary>
        /// Same check for a named command that is not an <see cref="ICommand"/> instance — the
        /// custom-response dispatch path: identical tier semantics to the overload above,
        /// with the required tier read from the (guild, <paramref name="commandName"/>)
        /// <c>CommandMetaData</c> override and <paramref name="defaultPermissionTier"/> as the
        /// fallback, so a custom response's required tier is adjustable at runtime exactly like a
        /// built-in's.
        /// </summary>
        Task<PermissionCheckResult> CheckUserPermissions(ulong userDiscordId, ulong? guildId, TierOverrides overrides, string commandName, int defaultPermissionTier, IDatabaseContext database);

        /// <summary>The user's effective tier in the guild: stored database tier (0 if absent), raised by the bootstrap ids. Null guild: the DM scope tier, without a database read.</summary>
        Task<int> GetUserEffectiveTier(ulong userDiscordId, ulong? guildId, TierOverrides overrides, IDatabaseContext database);

        /// <summary>The command's required tier in the guild: database override if present, otherwise the command's built-in default. Null guild: always the built-in default, without a database read.</summary>
        Task<int> GetCommandRequiredTier(ulong? guildId, ICommand command, IDatabaseContext database);
    }
}
