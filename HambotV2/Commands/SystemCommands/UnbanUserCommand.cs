using Discord;
using HambotV2.Connections;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Lifts a user's bot ban in this guild. Deliberately without the ban command's escalation
    /// guard: lifting a restriction is a relief action, so any moderator may unban whatever a
    /// higher tier put in place (the guild owner can raise the command's tier if that is abused).
    /// </summary>
    internal class UnbanUserCommand : ICommand
    {
        public string Name => "unbanuser";
        public string Description => "Lift a user's ban from using the bot in this server";
        public string Usage => "unbanuser <user>";
        public int DefaultPermissionTier => PermissionTier.Moderator;
        public CommandCategory Category => CommandCategory.Moderation;

        /// <summary>Slash surface: the target picked from Discord's user picker.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand => new(
        [
            new ApplicationCommandOption("user", "The user to unban from the bot", ApplicationCommandOptionKind.User),
        ]);

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
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

            var ban = await context.DbContext.BotBans.FindAsync(guildId, targetDiscordId);
            if (ban is null)
            {
                await message.Channel.SendMessageAsync("That user is not banned from the bot in this server");
                return;
            }

            context.DbContext.BotBans.Remove(ban);
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Unbanned user {arguments.Get(0)} from using the bot");
        }
    }
}
