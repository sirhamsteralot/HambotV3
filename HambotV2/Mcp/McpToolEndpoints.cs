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

        private static CallToolResult ToToolResult(McpToolResult result) => new()
        {
            Content = [new TextContentBlock { Text = result.Text }],
            IsError = result.IsError
        };

        private static async Task<CallToolResult> ToToolResultAsync(Task<McpToolResult> result)
            => ToToolResult(await result);
    }
}
