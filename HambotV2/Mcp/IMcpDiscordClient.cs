using Discord;
using Discord.WebSocket;

namespace HambotV2.Mcp
{
    /// <summary>One guild of the shared client's cache, as reported by the <c>list_guilds</c> tool.</summary>
    internal sealed record DiscordGuildSummary(ulong Id, string Name);

    /// <summary>One channel message, as reported by the <c>get_recent_messages</c> tool.</summary>
    internal sealed record DiscordChannelMessage(ulong AuthorId, string AuthorName, DateTimeOffset Timestamp, string Content);

    /// <summary>
    /// The narrow Discord surface the MCP tools need: the bot's own user, the guild cache,
    /// and the REST reads/sends — a seam over the shared client so the tools are unit-testable
    /// without a live Discord connection.
    /// </summary>
    internal interface IMcpDiscordClient
    {
        /// <summary>The bot's own user; null while the gateway connection is still starting.</summary>
        IUser? CurrentUser { get; }

        IReadOnlyList<DiscordGuildSummary> GetGuilds();

        /// <summary>A text channel of the given guild the bot can read; null when the guild is unknown or has no accessible text channel.</summary>
        IMessageChannel? GetGuildTextChannel(ulong guildId);

        /// <summary>The channel's most recent messages, newest first; null when the channel is unknown or inaccessible.</summary>
        Task<IReadOnlyList<DiscordChannelMessage>?> GetRecentMessagesAsync(ulong channelId, int limit);

        /// <summary>Sends the plain text; false when the channel is unknown or inaccessible (send errors throw).</summary>
        Task<bool> TrySendMessageAsync(ulong channelId, string text);
    }

    /// <summary>Production surface over the shared <see cref="DiscordSocketClient"/>: the guild cache plus REST reads/sends.</summary>
    internal sealed class DiscordSocketMcpClient : IMcpDiscordClient
    {
        private readonly DiscordSocketClient _client;

        public DiscordSocketMcpClient(DiscordSocketClient client)
        {
            _client = client;
        }

        public IUser? CurrentUser => _client.CurrentUser;

        public IReadOnlyList<DiscordGuildSummary> GetGuilds()
        {
            return _client.Guilds
                .Select(guild => new DiscordGuildSummary(guild.Id, guild.Name))
                .ToList();
        }

        public IMessageChannel? GetGuildTextChannel(ulong guildId)
        {
            return _client.GetGuild(guildId)?
                .TextChannels
                .OrderBy(channel => channel.Position)
                .FirstOrDefault();
        }

        public async Task<IReadOnlyList<DiscordChannelMessage>?> GetRecentMessagesAsync(ulong channelId, int limit)
        {
            if (await _client.GetChannelAsync(channelId) is not IMessageChannel channel)
                return null;

            // Newest first, exactly as Discord returns them; only the messages the bot can already read.
            IEnumerable<IMessage> messages = await channel.GetMessagesAsync(limit).FlattenAsync();
            return messages
                .Select(message => new DiscordChannelMessage(
                    message.Author?.Id ?? 0,
                    message.Author?.Username ?? "unknown",
                    message.Timestamp,
                    message.Content))
                .ToList();
        }

        public async Task<bool> TrySendMessageAsync(ulong channelId, string text)
        {
            if (await _client.GetChannelAsync(channelId) is not IMessageChannel channel)
                return false;

            await channel.SendMessageAsync(text);
            return true;
        }
    }
}
