namespace HambotV2.Mcp
{
    /// <summary>
    /// The MCP endpoint's operator-facing state for the health snapshot:
    /// set once at startup by the hosted service, e.g. "disabled (Mcp:Enabled is false)"
    /// or "listening at http://0.0.0.0:2525/mcp".
    /// </summary>
    internal interface IMcpEndpointStatus
    {
        string State { get; }
    }

    internal sealed class McpEndpointStatus : IMcpEndpointStatus
    {
        private volatile string _state = "not started";

        public string State => _state;

        public void SetState(string state)
        {
            _state = state;
        }
    }
}
