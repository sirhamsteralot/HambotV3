using Discord.WebSocket;

namespace HambotV2.Watchdog
{
    /// <summary>
    /// The gateway connection state as seen by the health report and the watchdog — a
    /// narrow read-only view over the shared client so both consumers are unit-testable without
    /// a live gateway.
    /// </summary>
    internal interface IGatewayStatus
    {
        /// <summary>The gateway connection state, e.g. "Connected" or "Disconnected".</summary>
        string State { get; }

        /// <summary>True while the gateway connection is up.</summary>
        bool IsConnected { get; }
    }

    internal sealed class DiscordGatewayStatus : IGatewayStatus
    {
        private readonly DiscordSocketClient _client;

        public DiscordGatewayStatus(DiscordSocketClient client)
        {
            _client = client;
        }

        public string State => _client.ConnectionState.ToString();

        public bool IsConnected => _client.ConnectionState == Discord.ConnectionState.Connected;
    }
}
