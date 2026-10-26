namespace HambotV2.Mcp
{
    /// <summary>
    /// One MCP tool call's outcome: the result text plus whether it reports a failure —
    /// failing calls are returned as error results, never thrown.
    /// </summary>
    public sealed record McpToolResult(bool IsError, string Text)
    {
        public static McpToolResult Ok(string text) => new(false, text);

        public static McpToolResult Error(string text) => new(true, text);
    }
}
