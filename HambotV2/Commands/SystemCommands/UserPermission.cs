using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands.SystemCommands
{
    internal class UserPermission : ICommand
    {
        public string Name => "userpermission";

        public async Task ExecuteCommand(SocketMessage socketMessage, ICommandContext context)
        {
            string[] arguments = socketMessage.Content.Split(' ');

            if (arguments.Length < 2)
            {
                await socketMessage.Channel.SendMessageAsync($"Insufficient arguments...");
                return;
            }

            ulong discordIdParsed = 0;
            if (!ulong.TryParse(arguments[1], out discordIdParsed))
            {
                await socketMessage.Channel.SendMessageAsync($"Could not parse discord id from {arguments[1]}");
                return;
            }

            if (arguments.Length == 2)
            {
                var user = context.DbContext.Users.FirstOrDefault(item => item.DiscordId == discordIdParsed);
                if (user != null)
                {
                    await socketMessage.Channel.SendMessageAsync($"Permissions tier for user {arguments[1]}: {user.PermissionTier}");
                    return;
                }
                else
                {
                    await socketMessage.Channel.SendMessageAsync($"Permissions tier for user {arguments[1]}: 0");
                    return;
                }
                return;
            }

            if (arguments.Length >= 3)
            {
                if (int.TryParse(arguments[2], out var permissionsTier))
                {
                    var user = context.DbContext.Users.FirstOrDefault(item => item.DiscordId == discordIdParsed);
                    if (user != null)
                    {
                        user.PermissionTier = permissionsTier;
                    }
                    else
                    {
                        user = new Connections.DatabaseContext.User()
                        {
                            DiscordId = discordIdParsed,
                            PermissionTier = permissionsTier
                        };

                        context.DbContext.Users.Add(user);
                    }

                    await context.DbContext.SaveChangesAsync();
                    await socketMessage.Channel.SendMessageAsync($"Updated permissions for user {arguments[1]}");
                    return;
                }

                await socketMessage.Channel.SendMessageAsync($"Could not parse new Tier for user {arguments[1]} | {arguments[2]}");
                return;
            }

        }
    }
}
