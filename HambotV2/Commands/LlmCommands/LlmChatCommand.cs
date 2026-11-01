using Discord;

using HambotV2.Connections;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// The per-guild chat-context opt-in: when on, an LLM update triggered from this
    /// server includes recent messages from this server's readable channels only; off (the
    /// default) means no chat is read. Admin tier, guild-only.
    /// </summary>
    internal sealed class LlmChatCommand : ICommand
    {
        public string Name => "llmchat";
        public string Description => "Shows or sets this server's LLM chat-context opt-in";
        public string Usage => "llmchat [on|off]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the operation stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show the state, or: on, off");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // The opt-in is per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("The 'llmchat' setting is per-server and only available in servers.");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            var setting = context.DbContext.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);

            if (arguments.Count == 0)
            {
                await message.Channel.SendMessageAsync(setting?.LlmIncludeChat == true
                    ? "LLM chat context: ON for this server — !llmupdate run here includes recent messages from this server's readable channels."
                    : "LLM chat context: OFF for this server (default) — no chat is read.");
                return;
            }

            if (!arguments.TryGetString(0, out string action) || (action != "on" && action != "off"))
            {
                await message.Channel.SendMessageAsync($"Usage: {context.CommandIdentifier}{Usage}");
                return;
            }

            (await GuildSettingStore.GetOrAddAsync(context.DbContext, guildId)).LlmIncludeChat = action == "on";
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync(action == "on"
                ? "LLM chat context enabled for this server: !llmupdate run here includes recent messages from this server's readable channels."
                : "LLM chat context disabled for this server — no chat is read for updates run here.");
        }
    }
}
