using System;

namespace HambotV2.Commands
{
    /// <summary>
    /// The named permission tier scale (FR-33/FR-37). Tiers are ordered integers;
    /// a command may run when the user's effective tier is greater than or equal to the
    /// command's required tier. The bot owner tier (4) sits above every guild-level tier and
    /// can only be held via configuration — it can never be assigned through commands.
    /// </summary>
    internal static class PermissionTier
    {
        public const int Everyone = 0;
        public const int Moderator = 1;
        public const int Admin = 2;
        public const int Owner = 3;
        public const int BotOwner = 4;

        public const int Minimum = Everyone;
        public const int Maximum = BotOwner;

        public static bool IsValid(int tier) => tier >= Minimum && tier <= Maximum;

        public static string GetName(int tier) => tier switch
        {
            Everyone => "everyone",
            Moderator => "moderator",
            Admin => "admin",
            Owner => "owner",
            BotOwner => "bot owner",
            _ => "unknown"
        };
    }
}
