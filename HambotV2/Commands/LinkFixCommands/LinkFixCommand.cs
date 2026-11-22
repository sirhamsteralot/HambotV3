using Discord;

using HambotV2.Connections;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LinkFixCommands
{
    /// <summary>
    /// The per-guild link-repost opt-in: when on, this server's member messages carrying
    /// X/Twitter or Reddit links get one follow-up with the fixed-embed mirror links; off (the
    /// default) means nothing is scanned. The only guild-level knob of the feature — the mapping
    /// itself is global (<c>!linkfixmap</c>). Admin tier, guild-only.
    /// </summary>
    internal sealed class LinkFixCommand : ICommand
    {
        public string Name => "linkfix";
        public string Description => "Shows or sets this server's fixed-embed link reposting";
        public string Usage => "linkfix [on|off]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.PermissionsAndSettings;

        /// <summary>Slash surface: the operation stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show the state, or: on, off");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // The opt-in is per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("The 'linkfix' setting is per-server and only available in servers.");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            var setting = context.DbContext.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);

            if (arguments.Count == 0)
            {
                await message.Channel.SendMessageAsync(setting?.LinkFixEnabled == true
                    ? "Link reposting: ON for this server — X/Twitter and Reddit links in member messages get a follow-up reply with fixed-embed mirror links."
                    : "Link reposting: OFF for this server (default) — no message is scanned.");
                return;
            }

            if (arguments.Count != 1 || !arguments.TryGetString(0, out string action) || (action != "on" && action != "off"))
            {
                await message.Channel.SendMessageAsync($"Usage: {context.CommandIdentifier}{Usage}");
                return;
            }

            (await GuildSettingStore.GetOrAddAsync(context.DbContext, guildId)).LinkFixEnabled = action == "on";
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync(action == "on"
                ? "Link reposting enabled for this server: X/Twitter and Reddit links in member messages get a follow-up reply with fixed-embed mirror links."
                : "Link reposting disabled for this server — no message is scanned.");
        }
    }
}
