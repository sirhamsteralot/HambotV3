using Discord;
using HambotV2.Commands.ReminderCommands;
using HambotV2.Connections;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Bans a user from using the bot in this guild, temporarily or permanently. Escalation
    /// guard: only users strictly below the invoker's effective tier can be banned — never the
    /// invoker themselves, never the guild owner or the configured bot owner (the implicit
    /// owner tiers are exempt from enforcement, so their records would be inert, and a ban
    /// would leave them unable to lift it from chat). Leading duration tokens — the reminder
    /// duration forms — make the ban temporary; omitting them makes it permanent.
    /// </summary>
    internal class BanUserCommand : ICommand
    {
        public string Name => "banuser";
        public string Description => "Ban a user from using the bot in this server, temporarily or permanently";
        public string Usage => "banuser <user> [duration]";
        public int DefaultPermissionTier => PermissionTier.Moderator;
        public CommandCategory Category => CommandCategory.Moderation;

        /// <summary>Slash surface: the target picked from Discord's user picker, the duration as free text.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand => new(
        [
            new ApplicationCommandOption("user", "The user to ban from the bot", ApplicationCommandOptionKind.User),
            new ApplicationCommandOption("duration", "How long the ban lasts (e.g. 1d 12h) — omit for a permanent ban", ApplicationCommandOptionKind.Text, Required: false),
        ]);

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Bans are per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (arguments.Count == 0)
            {
                await message.Channel.SendMessageAsync($"Insufficient arguments. Usage: !{Usage} — <user> is a Discord id or @mention, [duration] uses the reminder forms (e.g. 1d 12h)");
                return;
            }

            if (!arguments.TryGetDiscordUserId(0, out ulong targetDiscordId))
            {
                await message.Channel.SendMessageAsync($"Could not parse a Discord user id from {arguments.Get(0)} (a raw id or an @mention like <@123456789> is expected)");
                return;
            }

            if (targetDiscordId == message.Author.Id)
            {
                await message.Channel.SendMessageAsync("You cannot ban yourself from the bot");
                return;
            }

            if (targetDiscordId == context.Overrides.GuildOwnerId || targetDiscordId == context.Overrides.BotOwnerId)
            {
                await message.Channel.SendMessageAsync("The guild owner and the bot owner cannot be banned from the bot");
                return;
            }

            // Leading duration tokens make the ban temporary; the command takes nothing else.
            bool temporary = ReminderDurationParser.TryParse(arguments, 1, out TimeSpan duration, out int durationTokens);
            if (arguments.Count > 1 && (!temporary || arguments.Count > 1 + durationTokens))
            {
                await message.Channel.SendMessageAsync($"This command takes a user and an optional duration. Usage: !{Usage}");
                return;
            }

            DateTimeOffset? expiresAtUtc = temporary ? DateTimeOffset.UtcNow.Add(duration) : null;

            int invokerTier = context.GetUserEffectiveTier(message.Author.Id);
            int targetTier = context.GetUserEffectiveTier(targetDiscordId);
            if (targetTier >= invokerTier)
            {
                await message.Channel.SendMessageAsync("You can only ban users below your own tier from the bot");
                return;
            }

            var ban = await context.DbContext.BotBans.FindAsync(guildId, targetDiscordId);
            bool existed = ban is not null;
            ban ??= new DatabaseContext.BotBan { GuildId = guildId, DiscordId = targetDiscordId };

            ban.BannedByDiscordId = message.Author.Id;
            ban.CreatedAt = DateTime.UtcNow;
            ban.ExpiresAtUtc = expiresAtUtc;

            if (!existed)
                context.DbContext.BotBans.Add(ban);
            await context.DbContext.SaveChangesAsync();

            string lifetime = BotBans.DescribeLifetime(ban, DateTimeOffset.UtcNow);
            string verb = existed ? "Updated the bot ban for" : "Banned";
            await message.Channel.SendMessageAsync($"{verb} user {arguments.Get(0)} from using the bot: {lifetime}");
        }
    }
}
