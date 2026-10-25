using HambotV2.Connections;

namespace HambotV2.Commands
{
    internal interface ICooldownService
    {
        /// <summary>
        /// Decides whether the invocation may run and, if so, records its timestamp
        /// (NFR-12). The window is resolved from the invoking user's effective tier — the guarded tier
        /// from the permission check, so the throttle cannot be bypassed via permission lookups.
        /// A null <paramref name="guildId"/> means a direct message: the throttle key uses the
        /// sentinel guild id 0, which matches no real guild, so DM throttles never collide with any
        /// guild's (and the DM scope reads no guild state).
        /// </summary>
        CooldownCheck TryBeginInvocation(ulong? guildId, ulong userId, string commandName, int userEffectiveTier, IDatabaseContext database);
    }

    /// <summary>
    /// Result of a cooldown check: either the invocation is allowed (and its timestamp was
    /// recorded), or it is throttled and <see cref="RemainingWait"/> names how long the user
    /// must wait.
    /// </summary>
    internal readonly record struct CooldownCheck(bool IsAllowed, TimeSpan RemainingWait)
    {
        public static CooldownCheck Allowed { get; } = new(true, TimeSpan.Zero);
    }
}
