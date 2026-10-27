using Discord;
using HambotV2.Connections;
using HambotV2.SlashCommands;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Lists this guild's current bot bans: user, lifetime (permanent, or the absolute expiry
    /// and remaining time), and banned-by — soonest expiry first, permanent last. Expired
    /// records are lifted on sight (the same lazy cleanup the pipeline enforces).
    /// </summary>
    internal class BanListCommand : ICommand
    {
        private const int ListDisplayCap = 10;

        public string Name => "banlist";
        public string Description => "List the users currently banned from using the bot in this server";
        public string Usage => "banlist";
        public int DefaultPermissionTier => PermissionTier.Moderator;
        public CommandCategory Category => CommandCategory.Moderation;

        /// <summary>Slash surface: no arguments.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand => ApplicationCommandDeclaration.NoArguments;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var bans = await context.DbContext.BotBans.Where(ban => ban.GuildId == guildId).ToListAsync();

            var expired = bans.Where(ban => ban.ExpiresAtUtc is DateTimeOffset expires && expires <= now).ToList();
            foreach (var ban in expired)
                context.DbContext.BotBans.Remove(ban);
            if (expired.Count > 0)
                await context.DbContext.SaveChangesAsync();

            var active = bans.Except(expired)
                .OrderBy(ban => ban.ExpiresAtUtc ?? DateTimeOffset.MaxValue)
                .ToList();

            if (active.Count == 0)
            {
                await message.Channel.SendMessageAsync("No users are banned from the bot in this server");
                return;
            }

            IEnumerable<string> lines = active
                .Take(ListDisplayCap)
                .Select(ban => $"{ban.DiscordId} — {BotBans.DescribeLifetime(ban, now)} — banned by {ban.BannedByDiscordId}");

            string more = active.Count > ListDisplayCap ? $"\n… and {active.Count - ListDisplayCap} more" : string.Empty;
            await message.Channel.SendMessageAsync("Users banned from the bot in this server:\n" + string.Join('\n', lines) + more);
        }
    }
}
