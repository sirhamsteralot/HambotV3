using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands.SystemCommands
{
    internal class CommandPermission : ICommand
    {
        public string Name => "commandpermission";

        public async Task ExecuteCommand(SocketMessage socketMessage, ICommandContext context)
        {
            string[] arguments = socketMessage.Content.Split(' ');

            if (arguments.Length >= 3)
            {
                if (int.TryParse(arguments[2], out var permissionsTier))
                {
                    var command = context.DbContext.Commands.First(item => item.CommandName == arguments[1]);
                    if (command != null)
                    {
                        command.PermissionTier = permissionsTier;
                    }
                    else
                    {
                        command = new Connections.DatabaseContext.CommandMetaData()
                        {
                            CommandName = Name,
                            PermissionTier = permissionsTier
                        };

                        context.DbContext.Commands.Add(command);
                    }

                    await context.DbContext.SaveChangesAsync();
                    await socketMessage.Channel.SendMessageAsync($"Updated permissions for command {arguments[1]}");
                    return;
                }

                await socketMessage.Channel.SendMessageAsync($"Could not parse new Tier for command {arguments[1]} | {arguments[2]}");
                return;
            } else if (arguments.Length == 2)
            {
                var command = context.DbContext.Commands.First(item => item.CommandName == arguments[1]);
                if (command != null)
                {
                    await socketMessage.Channel.SendMessageAsync($"Permissions tier for command {arguments[1]}: {command.PermissionTier}");
                    return;
                }
                else
                {
                    await socketMessage.Channel.SendMessageAsync($"Permissions tier for command {arguments[1]}: 0");
                    return;
                }
            }

            await socketMessage.Channel.SendMessageAsync($"Insufficient arguments...");
        }
    }
}
