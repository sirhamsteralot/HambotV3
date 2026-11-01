using Discord;

using HambotV2.Connections;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// The per-guild LLM chat-context channel allowlist: while rows exist, the LLM chat
    /// reader reads only the listed channels of this guild; an empty list (the default, or
    /// after clear) means all readable channels. Admin tier, guild-only.
    /// </summary>
    internal sealed class LlmChannelsCommand : ICommand
    {
        private readonly IGuildChannelLookup? _guildChannels;

        public LlmChannelsCommand(IGuildChannelLookup? guildChannels = null)
        {
            _guildChannels = guildChannels;
        }

        public string Name => "llmchannels";
        public string Description => "Manages this server's LLM chat-context channel allowlist";
        public string Usage => "llmchannels [add|remove <#channel|id>|clear]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the arguments stay free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("As on the text surface, e.g. 'add <#channel|id>', 'remove <#channel|id>', or clear");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // The allowlist is per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("The 'llmchannels' setting is per-server and only available in servers.");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            var rows = context.DbContext.GuildLlmChannels.Where(row => row.GuildId == guildId).ToList();

            if (arguments.Count == 0)
            {
                string listed = string.Join(", ", rows.Select(row => "<#" + row.ChannelId + ">"));
                await message.Channel.SendMessageAsync(rows.Count > 0
                    ? $"LLM chat channels for this server ({rows.Count}): {listed} — only these channels are read"
                    : "LLM chat channels: all readable channels (no allowlist set)");
                return;
            }

            string? action = arguments.Get(0);
            if (action == "clear" && arguments.Count == 1)
            {
                context.DbContext.GuildLlmChannels.RemoveRange(rows);
                await context.DbContext.SaveChangesAsync();
                await message.Channel.SendMessageAsync("LLM channel allowlist cleared — all readable channels are read again (when LLM chat context is on)");
                return;
            }

            if ((action == "add" || action == "remove") && arguments.Count == 2)
            {
                if (!arguments.TryGetDiscordChannelId(1, out ulong channelId))
                {
                    await message.Channel.SendMessageAsync($"That does not look like a channel — pass a channel mention (e.g. #general) or a raw channel id. Usage: {context.CommandIdentifier}{Usage}");
                    return;
                }

                if (_guildChannels is not null && !_guildChannels.IsTextChannelOf(guildId, channelId))
                {
                    await message.Channel.SendMessageAsync("That is not a text channel of this server.");
                    return;
                }

                var existing = rows.FirstOrDefault(row => row.ChannelId == channelId);
                if (action == "add")
                {
                    if (existing is not null)
                    {
                        await message.Channel.SendMessageAsync($"<#{channelId}> is already on the LLM channel allowlist.");
                        return;
                    }

                    context.DbContext.GuildLlmChannels.Add(new DatabaseContext.GuildLlmChannel { GuildId = guildId, ChannelId = channelId });
                    await context.DbContext.SaveChangesAsync();
                    await message.Channel.SendMessageAsync($"Added <#{channelId}> to the LLM channel allowlist — only listed channels are read (when LLM chat context is on).");
                    return;
                }

                if (existing is null)
                {
                    await message.Channel.SendMessageAsync($"<#{channelId}> is not on the LLM channel allowlist.");
                    return;
                }

                context.DbContext.GuildLlmChannels.Remove(existing);
                await context.DbContext.SaveChangesAsync();
                await message.Channel.SendMessageAsync($"Removed <#{channelId}> from the LLM channel allowlist.");
                return;
            }

            await message.Channel.SendMessageAsync($"Usage: {context.CommandIdentifier}{Usage}");
        }
    }
}
