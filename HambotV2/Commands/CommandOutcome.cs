namespace HambotV2.Commands
{
    /// <summary>
    /// Result of handling one command message, recorded in the structured audit log (roadmap T12,
    /// NFR-11). Exactly one <see cref="CommandOutcome"/> is logged per message that reaches the
    /// command pipeline.
    /// </summary>
    internal enum CommandOutcome
    {
        /// <summary>The command ran (help text reply, permission tier change, ...).</summary>
        Executed,

        /// <summary>The command was resolved but the user's effective tier was insufficient.</summary>
        DeniedPermission,

        /// <summary>The command name is not registered.</summary>
        UnknownCommand,

        /// <summary>The message was identifier-only (e.g. a bare "!") and is ignored silently.</summary>
        Ignored,

        /// <summary>The command exists but the user is on cooldown (roadmap T13).</summary>
        Throttled,

        /// <summary>The pipeline caught an exception; the user got the generic error reply.</summary>
        Failed,
    }
}
