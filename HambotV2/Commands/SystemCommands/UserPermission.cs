using Discord;
using HambotV2.Connections;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Shows or sets a user's permission tier. Escalation guard (roadmap T16, FR-36): the invoking
    /// user may only assign tiers strictly below their own effective tier, and can never modify
    /// their own tier or that of an equal/higher-tiered user. Bootstrap admins (guild owner /
    /// configured admin) always act at owner tier, so a fresh guild has a working first admin.
    /// </summary>
    internal class UserPermission : ICommand
    {
        public string Name => "userpermission";
        public string Description => "Show or set a user's permission tier (0 everyone, 1 moderator, 2 admin, 3 owner)";
        public string Usage => "userpermission <user> [tier]";
        public int DefaultPermissionTier => PermissionTier.Admin;

        public async Task ExecuteCommand(IMessage socketMessage, ICommandContext context)
        {
            string[] arguments = socketMessage.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (arguments.Length < 2)
            {
                await socketMessage.Channel.SendMessageAsync($"Insufficient arguments. Usage: !{Usage} — <user> is a Discord id or @mention");
                return;
            }

            if (!DiscordUserId.TryParse(arguments[1], out ulong targetDiscordId))
            {
                await socketMessage.Channel.SendMessageAsync($"Could not parse a Discord user id from {arguments[1]} (a raw id or an @mention like <@123456789> is expected)");
                return;
            }

            int invokerTier = context.GetUserEffectiveTier(socketMessage.Author.Id);

            if (arguments.Length == 2)
            {
                int storedTier = GetStoredUserTier(context, targetDiscordId);
                int effectiveTier = context.Overrides.EffectiveTierOf(targetDiscordId, storedTier);
                string? overrideLabel = context.Overrides.DescribeOverride(targetDiscordId, storedTier);
                string overrideNote = overrideLabel != null ? $" ({overrideLabel})" : string.Empty;

                await socketMessage.Channel.SendMessageAsync($"Permission tier for user {arguments[1]}: {effectiveTier}{overrideNote}");
                return;
            }

            if (!int.TryParse(arguments[2], out int newTier) || !PermissionTier.IsValid(newTier))
            {
                await socketMessage.Channel.SendMessageAsync($"'{arguments[2]}' is not a valid permission tier. Valid tiers: 0 (everyone), 1 (moderator), 2 (admin), 3 (owner)");
                return;
            }

            if (targetDiscordId == socketMessage.Author.Id)
            {
                await socketMessage.Channel.SendMessageAsync("You cannot modify your own permission tier");
                return;
            }

            int targetStoredTier = GetStoredUserTier(context, targetDiscordId);
            int targetEffectiveTier = context.Overrides.EffectiveTierOf(targetDiscordId, targetStoredTier);

            if (targetEffectiveTier >= invokerTier)
            {
                await socketMessage.Channel.SendMessageAsync("You can only modify the permission tier of users below your own tier");
                return;
            }

            if (newTier >= invokerTier)
            {
                await socketMessage.Channel.SendMessageAsync("You can only assign permission tiers strictly below your own tier");
                return;
            }

            PermissionPersistence.UpsertUserTier(context.DbContext, targetDiscordId, newTier);
            await context.DbContext.SaveChangesAsync();
            await socketMessage.Channel.SendMessageAsync($"Updated permission tier for user {arguments[1]} to {newTier}");
        }

        private static int GetStoredUserTier(ICommandContext context, ulong discordId)
        {
            return context.DbContext.Users.FirstOrDefault(user => user.DiscordId == discordId)?.PermissionTier
                ?? PermissionTier.Everyone;
        }
    }
}
