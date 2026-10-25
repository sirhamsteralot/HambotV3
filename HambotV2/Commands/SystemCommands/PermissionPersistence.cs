using HambotV2.Connections;
using System.Linq;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Shared "upsert tier" logic of the two permission-management commands (roadmap T7).
    /// The escalation guards themselves live in the commands, next to their user-facing errors.
    /// </summary>
    internal static class PermissionPersistence
    {
        public static void UpsertUserTier(IDatabaseContext database, ulong guildId, ulong discordId, int permissionTier)
        {
            var user = database.Users.FirstOrDefault(item => item.GuildId == guildId && item.DiscordId == discordId);
            if (user != null)
            {
                user.PermissionTier = permissionTier;
            }
            else
            {
                database.Users.Add(new DatabaseContext.User
                {
                    GuildId = guildId,
                    DiscordId = discordId,
                    PermissionTier = permissionTier
                });
            }
        }

        public static void UpsertCommandTier(IDatabaseContext database, ulong guildId, string commandName, int permissionTier)
        {
            var command = database.Commands.FirstOrDefault(item => item.GuildId == guildId && item.CommandName == commandName);
            if (command != null)
            {
                command.PermissionTier = permissionTier;
            }
            else
            {
                database.Commands.Add(new DatabaseContext.CommandMetaData
                {
                    GuildId = guildId,
                    CommandName = commandName,
                    PermissionTier = permissionTier
                });
            }
        }
    }
}
