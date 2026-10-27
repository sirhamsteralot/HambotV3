using Discord;

namespace HambotV2.Mcp
{
    /// <summary>
    /// The MCP tool surface (design §3.11): thin wrappers over the existing components —
    /// the shared client, the presence service, and the virtual command invocation seam —
    /// so nothing re-implements command or data logic. Implementations never throw: every
    /// call returns its outcome, failing calls as error results. Public because the
    /// SDK-discovered tool wrappers live at this boundary; the implementation is internal.
    /// </summary>
    public interface IMcpTools
    {
        McpToolResult BotStatus();

        McpToolResult ListGuilds();

        McpToolResult ListChannels(ulong guildId);

        McpToolResult ListCommands();

        Task<McpToolResult> SetPresenceAsync(string type, string text);

        Task<McpToolResult> SetBioAsync(string text);

        Task<McpToolResult> GetRecentMessagesAsync(ulong channelId, int limit);

        Task<McpToolResult> SendMessageAsync(ulong channelId, string text);

        Task<McpToolResult> RunCommandAsync(ulong guildId, string commandLine);
    }
}
