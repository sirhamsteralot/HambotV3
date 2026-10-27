using HambotV2.Commands.ReminderCommands;
using HambotV2.Connections;

namespace HambotV2.Commands
{
    /// <summary>
    /// The shared bot-ban lookup: an expired temporary ban is lifted the moment a check
    /// encounters it (the row is deleted), so no scheduled cleanup job exists — expiry
    /// is enforced lazily, at read time.
    /// </summary>
    internal static class BotBans
    {
        /// <summary>The active ban record for the user in the guild, or null when none; an expired record is deleted on sight.</summary>
        public static async Task<DatabaseContext.BotBan?> FindActiveAsync(IDatabaseContext database, ulong guildId, ulong discordId, DateTimeOffset now)
        {
            var ban = await database.BotBans.FindAsync(guildId, discordId);
            if (ban is null)
                return null;

            if (ban.ExpiresAtUtc is DateTimeOffset expires && expires <= now)
            {
                database.BotBans.Remove(ban);
                await database.SaveChangesAsync();
                return null;
            }

            return ban;
        }

        /// <summary>Short reply wording for a ban's lifetime: "permanent", or the absolute UTC expiry with the remaining time.</summary>
        public static string DescribeLifetime(DatabaseContext.BotBan ban, DateTimeOffset now)
        {
            if (ban.ExpiresAtUtc is not DateTimeOffset expires)
                return "permanent";

            string remaining = ReminderIntervalFormat.Format(expires - now);
            return $"until {expires:yyyy-MM-dd HH:mm} UTC (in {remaining})";
        }
    }
}
