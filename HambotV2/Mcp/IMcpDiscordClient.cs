using Discord;
using Discord.WebSocket;

namespace HambotV2.Mcp
{
    /// <summary>One guild of the shared client's cache, as reported by the <c>list_guilds</c> tool.</summary>
    internal sealed record DiscordGuildSummary(ulong Id, string Name);

    /// <summary>One channel of a guild's cache, as reported by the <c>list_channels</c> tool.</summary>
    internal sealed record DiscordChannelSummary(ulong Id, string Name, string Type);

    /// <summary>One channel message, as reported by the <c>get_recent_messages</c> tool.</summary>
    internal sealed record DiscordChannelMessage(ulong AuthorId, string AuthorName, DateTimeOffset Timestamp, string Content);

    /// <summary>One guild member, as reported by the <c>get_user_profile</c> tool.</summary>
    internal sealed record DiscordMemberSummary(string Username, string? DisplayName, string? Nickname, IReadOnlyList<string> Roles, DateTimeOffset? JoinedAt);

    /// <summary>One member search result, as reported by the <c>search_users</c> tool.</summary>
    internal sealed record DiscordUserSummary(ulong Id, string Username, string? DisplayName, string? Nickname);

    /// <summary>
    /// The narrow Discord surface the MCP tools need: the bot's own user, the guild cache,
    /// the member reads/searches, and the REST reads/sends — a seam over the shared client so
    /// the tools are unit-testable without a live Discord connection.
    /// </summary>
    internal interface IMcpDiscordClient
    {
        /// <summary>The bot's own user; null while the gateway connection is still starting.</summary>
        IUser? CurrentUser { get; }

        IReadOnlyList<DiscordGuildSummary> GetGuilds();

        /// <summary>The guild's cached channels; null when the guild is unknown.</summary>
        IReadOnlyList<DiscordChannelSummary>? GetGuildChannels(ulong guildId);

        /// <summary>A text channel of the given guild the bot can read; null when the guild is unknown or has no accessible text channel.</summary>
        IMessageChannel? GetGuildTextChannel(ulong guildId);

        /// <summary>The channel's most recent messages, newest first; null when the channel is unknown or inaccessible.</summary>
        Task<IReadOnlyList<DiscordChannelMessage>?> GetRecentMessagesAsync(ulong channelId, int limit);

        /// <summary>One member of the guild; null when the guild is unknown or the user is not a member.</summary>
        Task<DiscordMemberSummary?> GetGuildMemberAsync(ulong guildId, ulong userId);

        /// <summary>Members matching the query (username, display name, nickname); null when the guild is unknown.</summary>
        Task<IReadOnlyList<DiscordUserSummary>?> SearchUsersAsync(ulong guildId, string query, int limit);

        /// <summary>Ids of the guild's text channels; null when the guild is unknown. Accessibility is resolved per read.</summary>
        IReadOnlyList<ulong>? GetGuildMessageChannelIds(ulong guildId);

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

        public IReadOnlyList<DiscordChannelSummary>? GetGuildChannels(ulong guildId)
        {
            SocketGuild? guild = _client.GetGuild(guildId);
            if (guild is null)
                return null;

            return guild.Channels
                .OrderBy(channel => channel.Position)
                .Select(channel => new DiscordChannelSummary(channel.Id, channel.Name, DescribeChannelType(channel)))
                .ToList();
        }

        // Specific before base: threads/news/stages are text or voice channels, and media channels are forums.
        private static string DescribeChannelType(SocketGuildChannel channel) => channel switch
        {
            SocketThreadChannel => "thread",
            SocketStageChannel => "stage",
            SocketVoiceChannel => "voice",
            SocketNewsChannel => "news",
            SocketMediaChannel => "media",
            SocketForumChannel => "forum",
            SocketTextChannel => "text",
            SocketCategoryChannel => "category",
            _ => "other"
        };

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

        public async Task<DiscordMemberSummary?> GetGuildMemberAsync(ulong guildId, ulong userId)
        {
            SocketGuild? guild = _client.GetGuild(guildId);
            if (guild is null)
                return null;

            // The member cache stays empty without the GuildMembers intent; the cache-then-REST
            // fetch is the reliable path.
            IGuildUser? user = await ((IGuild)guild).GetUserAsync(userId, CacheMode.AllowDownload);
            if (user is null)
                return null;

            return new DiscordMemberSummary(
                user.Username,
                user.GlobalName,
                user.Nickname,
                user.RoleIds
                    .Select(guild.GetRole)
                    .Where(role => role is not null && role.Id != guild.EveryoneRole.Id)
                    .Select(role => role.Name)
                    .ToList(),
                user.JoinedAt);
        }

        public async Task<IReadOnlyList<DiscordUserSummary>?> SearchUsersAsync(ulong guildId, string query, int limit)
        {
            SocketGuild? guild = _client.GetGuild(guildId);
            if (guild is null)
                return null;

            IReadOnlyCollection<IGuildUser> users = await guild.SearchUsersAsync(query, limit);
            return users
                .Select(user => new DiscordUserSummary(user.Id, user.Username, user.GlobalName, user.Nickname))
                .ToList();
        }

        public IReadOnlyList<ulong>? GetGuildMessageChannelIds(ulong guildId)
        {
            // News channels derive from SocketTextChannel, so both readable channel kinds are covered.
            return _client.GetGuild(guildId)?
                .Channels
                .OfType<SocketTextChannel>()
                .Select(channel => channel.Id)
                .ToList();
        }
    }
}
