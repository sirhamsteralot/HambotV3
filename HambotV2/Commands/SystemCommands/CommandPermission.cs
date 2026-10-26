using Discord;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Shows or sets the permission tier a command requires. Escalation guard: the
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

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Command tiers are per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (!arguments.TryGetString(0, out string commandNameArgument))
            {
                await message.Channel.SendMessageAsync($"Insufficient arguments. Usage: !{Usage}");
                return;
            }

            string commandName = commandNameArgument.ToLowerInvariant();
            var commandMetaData = context.DbContext.Commands.FirstOrDefault(item => item.GuildId == guildId && item.CommandName == commandName);

            if (arguments.Get(1) is null)
            {
                await message.Channel.SendMessageAsync(commandMetaData != null
                    ? $"Required permission tier for command {commandName}: {commandMetaData.PermissionTier}"
                    : $"No permission tier override set for command {commandName}; its built-in default applies");
                return;
            }

            if (!arguments.TryGetInt(1, out int newTier) || !PermissionTier.IsValid(newTier))
            {
                await message.Channel.SendMessageAsync($"'{arguments.Get(1)}' is not a valid permission tier. Valid tiers: 0 (everyone), 1 (moderator), 2 (admin), 3 (owner)");
                return;
            }

            int invokerTier = context.GetUserEffectiveTier(message.Author.Id);
            int currentRequiredTier = commandMetaData?.PermissionTier ?? PermissionTier.Everyone;

            if (currentRequiredTier >= invokerTier)
            {
                await message.Channel.SendMessageAsync("You can only change the required tier of commands that currently sit below your own tier");
                return;
            }

            if (newTier >= invokerTier)
            {
                await message.Channel.SendMessageAsync("You can only assign permission tiers strictly below your own tier");
                return;
            }

            PermissionPersistence.UpsertCommandTier(context.DbContext, guildId, commandName, newTier);
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Updated required permission tier for command {commandName} to {newTier}");
        }
    }
}
