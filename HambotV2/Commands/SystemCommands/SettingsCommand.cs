using Discord;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Manages the invoking guild's bot settings (roadmap T14, FR-26). Guild overrides fall back to
    /// the global defaults from appsettings.json when unset. Admin-only by default, mirroring the
    /// permission-management commands (FR-35); the bot owner can manage any guild by running the
    /// command there. Written on the shared argument-parsing helper (roadmap T15).
    /// </summary>
    internal class SettingsCommand : ICommand
    {
        private const int MaxIdentifierLength = 5;

        public string Name => "settings";
        public string Description => "Show this server's bot settings, or set the command identifier override";
        public string Usage => "settings [commandidentifier <value>|commandidentifier reset]";
        public int DefaultPermissionTier => PermissionTier.Admin;

        public async Task ExecuteCommand(IMessage socketMessage, ICommandContext context)
        {
            // Guild settings are per-guild state (roadmap T14); in the DM scope (FR-25) there is no
            // guild to manage and no state may be written, so the command declines.
            if (context.GuildId is not ulong guildId)
            {
                await socketMessage.Channel.SendMessageAsync("This command manages per-server settings and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(socketMessage.Content);

            if (arguments.Count == 0)
            {
                await SendOverview(socketMessage, context, guildId);
                return;
            }

            if (!arguments.TryGetString(0, out string settingName) || settingName != "commandidentifier")
            {
                await socketMessage.Channel.SendMessageAsync($"Unknown setting '{arguments.Get(0)}'. Usage: !{Usage}");
                return;
            }

            if (!arguments.TryGetString(1, out string action))
            {
                await socketMessage.Channel.SendMessageAsync($"Missing value. Usage: !{Usage}");
                return;
            }

            if (action == "reset")
            {
                context.GuildSettings.SetCommandIdentifier(guildId, null, context.DbContext);
                await context.DbContext.SaveChangesAsync();
                await socketMessage.Channel.SendMessageAsync("Command identifier reset to the global default");
                return;
            }

            if (action.Length > MaxIdentifierLength)
            {
                await socketMessage.Channel.SendMessageAsync($"The command identifier must be at most {MaxIdentifierLength} characters long");
                return;
            }

            context.GuildSettings.SetCommandIdentifier(guildId, action, context.DbContext);
            await context.DbContext.SaveChangesAsync();
            await socketMessage.Channel.SendMessageAsync($"Updated the command identifier for this server to {action}");
        }

        private static async Task SendOverview(IMessage socketMessage, ICommandContext context, ulong guildId)
        {
            var setting = context.DbContext.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);

            if (setting?.CommandIdentifier != null)
                await socketMessage.Channel.SendMessageAsync($"Command identifier for this server: {setting.CommandIdentifier} (server override)");
            else
                await socketMessage.Channel.SendMessageAsync($"Command identifier: {context.CommandIdentifier} (global default)");
        }
    }
}
