using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HambotV2.Mcp
{
    /// <summary>
    /// The SDK-discovered MCP tool wrappers: one public method per tool over
    /// <see cref="IMcpTools"/>, with the argument and result shapes the protocol carries.
    /// Public and attribute-driven because the ModelContextProtocol SDK discovers and
    /// activates tool types through public constructors; all logic lives behind the interface.
    /// </summary>
    [McpServerToolType]
    public sealed class McpToolEndpoints
    {
        private readonly IMcpTools _tools;

        public McpToolEndpoints(IMcpTools tools)
        {
            _tools = tools;
        }

        [McpServerTool(Name = "bot_status")]
        [Description("The bot's build version, uptime, gateway state, guild count, presence, and bio.")]
        public CallToolResult BotStatus() => ToToolResult(_tools.BotStatus());

        [McpServerTool(Name = "list_guilds")]
        [Description("The guilds the bot is a member of, one '<id> <name>' per line.")]
        public CallToolResult ListGuilds() => ToToolResult(_tools.ListGuilds());

        [McpServerTool(Name = "list_channels")]
        [Description("The guild's cached channels, one '<id> <name> (<type>)' per line — the discoverable channel ids for get_recent_messages and send_message.")]
        public CallToolResult ListChannels(ulong guildId) => ToToolResult(_tools.ListChannels(guildId));

        [McpServerTool(Name = "list_commands")]
        [Description("The bot's registered commands — name, default permission tier, description, and usage — for use with run_command, which takes the command line without the identifier.")]
        public CallToolResult ListCommands() => ToToolResult(_tools.ListCommands());

        [McpServerTool(Name = "set_presence")]
        [Description("Sets the bot's activity. Type: playing, watching, listening, or competing; text at most 128 characters.")]
        public Task<CallToolResult> SetPresenceAsync(string type, string text)
            => ToToolResultAsync(_tools.SetPresenceAsync(type, text));

        [McpServerTool(Name = "set_bio")]
        [Description("Sets the bot's profile 'About Me' text, at most 190 characters; line breaks allowed.")]
        public Task<CallToolResult> SetBioAsync(string text)
            => ToToolResultAsync(_tools.SetBioAsync(text));

        [McpServerTool(Name = "get_recent_messages")]
        [Description("The channel's most recent messages, newest first, capped at 50, within the bot's existing channel access.")]
        public Task<CallToolResult> GetRecentMessagesAsync(ulong channelId, int limit)
            => ToToolResultAsync(_tools.GetRecentMessagesAsync(channelId, limit));

        [McpServerTool(Name = "send_message")]
        [Description("Sends a plain-text message to a channel, at most 2000 characters; no files or embeds.")]
        public Task<CallToolResult> SendMessageAsync(ulong channelId, string text)
            => ToToolResultAsync(_tools.SendMessageAsync(channelId, text));

        [McpServerTool(Name = "run_command")]
        [Description("Runs a bot command line (without the command identifier) in a guild as the operator's agent, at that guild's configured agent tier (default the lowest); the command's reply is returned, never posted to a channel.")]
        public Task<CallToolResult> RunCommandAsync(ulong guildId, string commandLine)
            => ToToolResultAsync(_tools.RunCommandAsync(guildId, commandLine));

        [McpServerTool(Name = "search_users")]
        [Description("Searches a guild's members by username, display name, or nickname (case-insensitive), returning up to 25 '<id> <username>' matches; use the id with the member lookup tools.")]
        public Task<CallToolResult> SearchUsersAsync(ulong guildId, string query, int limit)
            => ToToolResultAsync(_tools.SearchUsersAsync(guildId, query, limit));

        [McpServerTool(Name = "get_user_profile")]
        [Description("A read-only overview of one member: Discord identity (username, nickname, roles, join date) plus the bot's records in that guild — permission tier, active bot ban, quotes added, and last-modified custom responses. Reminders are never included.")]
        public Task<CallToolResult> GetUserProfileAsync(ulong guildId, ulong userId)
            => ToToolResultAsync(_tools.GetUserProfileAsync(guildId, userId));

        [McpServerTool(Name = "get_user_quotes")]
        [Description("The quotes one member added in a guild, newest first, number-prefixed, capped at 50 entries.")]
        public Task<CallToolResult> GetUserQuotesAsync(ulong guildId, ulong userId)
            => ToToolResultAsync(_tools.GetUserQuotesAsync(guildId, userId));

        [McpServerTool(Name = "get_user_messages")]
        [Description("A member's most recent messages across the guild's bot-readable channels, newest first, capped at 50.")]
        public Task<CallToolResult> GetUserMessagesAsync(ulong guildId, ulong userId, int limit)
            => ToToolResultAsync(_tools.GetUserMessagesAsync(guildId, userId, limit));

        private static CallToolResult ToToolResult(McpToolResult result) => new()
        {
            Content = [new TextContentBlock { Text = result.Text }],
            IsError = result.IsError
        };

        private static async Task<CallToolResult> ToToolResultAsync(Task<McpToolResult> result)
            => ToToolResult(await result);
    }
}
