using Discord;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Shows or sets a user's permission tier. Escalation guard: the invoking
    /// user may only assign tiers strictly below their own effective tier, and can never modify
    /// their own tier or that of an equal/higher-tiered user. The guild owner always acts at
    /// owner tier, so a fresh guild has a working first admin.
    /// </summary>
    internal class UserPermission : ICommand
    {
        public string Name => "userpermission";
        public string Description => "Show or set a user's permission tier (0 everyone, 1 moderator, 2 admin, 3 owner)";
        public string Usage => "userpermission <user> [tier]";
        public int DefaultPermissionTier => PermissionTier.Admin;

        /// <summary>Slash surface: the target picked from Discord's user picker, the tier as an integer.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand => new(
        [
            new ApplicationCommandOption("user", "The user whose tier to show or set", ApplicationCommandOptionKind.User),
            new ApplicationCommandOption("tier", "The new tier: 0 everyone, 1 moderator, 2 admin, 3 owner", ApplicationCommandOptionKind.Integer, Required: false),
        ]);

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // User tiers are per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (arguments.Count == 0)
            {
                await message.Channel.SendMessageAsync($"Insufficient arguments. Usage: !{Usage} — <user> is a Discord id or @mention");
                return;
            }

            if (!arguments.TryGetDiscordUserId(0, out ulong targetDiscordId))
            {
                await message.Channel.SendMessageAsync($"Could not parse a Discord user id from {arguments.Get(0)} (a raw id or an @mention like <@123456789> is expected)");
                return;
            }

            if (arguments.Get(1) is null)
            {
                int storedTier = GetStoredUserTier(context, guildId, targetDiscordId);
                int effectiveTier = context.Overrides.EffectiveTierOf(targetDiscordId, storedTier);
                string? overrideLabel = context.Overrides.DescribeOverride(targetDiscordId, storedTier);
                string overrideNote = overrideLabel != null ? $" ({overrideLabel})" : string.Empty;

                await message.Channel.SendMessageAsync($"Permission tier for user {arguments.Get(0)}: {effectiveTier}{overrideNote}");
                return;
            }

            if (!arguments.TryGetInt(1, out int newTier) || !PermissionTier.IsValid(newTier))
            {
                await message.Channel.SendMessageAsync($"'{arguments.Get(1)}' is not a valid permission tier. Valid tiers: 0 (everyone), 1 (moderator), 2 (admin), 3 (owner)");
                return;
            }

            if (targetDiscordId == message.Author.Id)
            {
                await message.Channel.SendMessageAsync("You cannot modify your own permission tier");
                return;
            }

            int invokerTier = context.GetUserEffectiveTier(message.Author.Id);
            int targetStoredTier = GetStoredUserTier(context, guildId, targetDiscordId);
            int targetEffectiveTier = context.Overrides.EffectiveTierOf(targetDiscordId, targetStoredTier);

            if (targetEffectiveTier >= invokerTier)
            {
                await message.Channel.SendMessageAsync("You can only modify the permission tier of users below your own tier");
                return;
            }

            if (newTier >= invokerTier)
            {
                await message.Channel.SendMessageAsync("You can only assign permission tiers strictly below your own tier");
                return;
            }

            PermissionPersistence.UpsertUserTier(context.DbContext, guildId, targetDiscordId, newTier);
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Updated permission tier for user {arguments.Get(0)} to {newTier}");
        }

        private static int GetStoredUserTier(ICommandContext context, ulong guildId, ulong discordId)
        {
            return context.DbContext.Users.FirstOrDefault(user => user.GuildId == guildId && user.DiscordId == discordId)?.PermissionTier
                ?? PermissionTier.Everyone;
        }
    }
}
