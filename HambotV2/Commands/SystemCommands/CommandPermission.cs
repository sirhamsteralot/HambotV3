using Discord;
using HambotV2.Connections;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Shows or sets the permission tier a command requires. Escalation guard (roadmap T16): the
    /// invoking user may only assign required tiers strictly below their own effective tier, and
    /// only to commands whose current required tier is strictly below their own — so nobody can
    /// unlock or lock commands at or above their own level.
    /// </summary>
    internal class CommandPermission : ICommand
    {
        public string Name => "commandpermission";
        public string Description => "Show or set the permission tier a command requires (0 everyone, 1 moderator, 2 admin, 3 owner)";
        public string Usage => "commandpermission <commandName> [tier]";
        public int DefaultPermissionTier => PermissionTier.Admin;

        public async Task ExecuteCommand(IMessage socketMessage, ICommandContext context)
        {
            string[] arguments = socketMessage.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (arguments.Length < 2)
            {
                await socketMessage.Channel.SendMessageAsync($"Insufficient arguments. Usage: !{Usage}");
                return;
            }

            string commandName = arguments[1].ToLowerInvariant();

            if (arguments.Length == 2)
            {
                var commandMetaData = context.DbContext.Commands.FirstOrDefault(item => item.CommandName == commandName);
                if (commandMetaData != null)
                {
                    await socketMessage.Channel.SendMessageAsync($"Required permission tier for command {commandName}: {commandMetaData.PermissionTier}");
                }
                else
                {
                    await socketMessage.Channel.SendMessageAsync($"No permission tier override set for command {commandName}; its built-in default applies");
                }
                return;
            }

            if (!int.TryParse(arguments[2], out int newTier) || !PermissionTier.IsValid(newTier))
            {
                await socketMessage.Channel.SendMessageAsync($"'{arguments[2]}' is not a valid permission tier. Valid tiers: 0 (everyone), 1 (moderator), 2 (admin), 3 (owner)");
                return;
            }

            int invokerTier = context.GetUserEffectiveTier(socketMessage.Author.Id);

            var existingMetaData = context.DbContext.Commands.FirstOrDefault(item => item.CommandName == commandName);
            int currentRequiredTier = existingMetaData?.PermissionTier ?? PermissionTier.Everyone;
            if (currentRequiredTier >= invokerTier)
            {
                await socketMessage.Channel.SendMessageAsync("You can only change the required tier of commands that currently sit below your own tier");
                return;
            }

            if (newTier >= invokerTier)
            {
                await socketMessage.Channel.SendMessageAsync("You can only assign permission tiers strictly below your own tier");
                return;
            }

            PermissionPersistence.UpsertCommandTier(context.DbContext, commandName, newTier);
            await context.DbContext.SaveChangesAsync();
            await socketMessage.Channel.SendMessageAsync($"Updated required permission tier for command {commandName} to {newTier}");
        }
    }
}
