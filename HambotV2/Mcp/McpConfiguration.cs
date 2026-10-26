namespace HambotV2.Mcp
{
    /// <summary>
    /// The MCP endpoint's configuration: <c>Mcp:Enabled</c> (default false),
    /// <c>Mcp:ListenUri</c> (default <see cref="DefaultListenUri"/>), and the secret
    /// <c>Mcp:ApiKey</c>. The endpoint is active only when enabled with a key set;
    /// an absent or invalid value falls back to the default, §3.4 treatment.
    /// </summary>
    internal sealed record McpConfiguration
    {
        public const string DefaultListenUri = "http://0.0.0.0:2525";

        public bool Enabled { get; init; }

        public string ListenUri { get; init; } = DefaultListenUri;

        public string ApiKey { get; init; } = string.Empty;

        /// <summary>True when the endpoint should run: enabled and a key is configured.</summary>
        public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

        public static McpConfiguration Parse(IConfiguration configuration)
        {
            return new McpConfiguration
            {
                Enabled = ParseEnabled(configuration["Mcp:Enabled"]),
                ListenUri = string.IsNullOrWhiteSpace(configuration["Mcp:ListenUri"])
                    ? DefaultListenUri
                    : configuration["Mcp:ListenUri"]!.Trim(),
                ApiKey = configuration["Mcp:ApiKey"] ?? string.Empty
            };
        }

        private static bool ParseEnabled(string? configuredValue)
        {
            return bool.TryParse(configuredValue, out bool enabled) && enabled;
        }
    }
}
