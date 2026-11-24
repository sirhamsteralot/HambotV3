namespace HambotV2.FuelUsageMonitor
{
    /// <summary>
    /// The receive path's configuration: the opt-in switch, the listen address and path, the
    /// upload secret, and the request bounds. An absent or invalid value falls back to its
    /// default, so a typo degrades to the shipped value instead of breaking the feature; the
    /// endpoint runs only when enabled with a token configured.
    /// </summary>
    internal sealed record FuelUsageMonitorSettings
    {
        internal const string DefaultListenUri = "http://0.0.0.0:2526";
        internal const string DefaultListenPath = "/fuel/upload";
        internal const int DefaultMaxUploadsPerMinute = 120;
        internal const int DefaultMaxQueryBytes = 8192;
        internal const int DefaultMaxBodyBytes = 8 * 1024 * 1024;

        public bool Enabled { get; init; }

        public string ListenUri { get; init; } = DefaultListenUri;

        public string ListenPath { get; init; } = DefaultListenPath;

        /// <summary>The bearer secret the phone app sends (a secret, never committed).</summary>
        public string UploadToken { get; init; } = string.Empty;

        public int MaxUploadsPerMinute { get; init; } = DefaultMaxUploadsPerMinute;

        public int MaxQueryBytes { get; init; } = DefaultMaxQueryBytes;

        public int MaxBodyBytes { get; init; } = DefaultMaxBodyBytes;

        /// <summary>True when the endpoint should run: enabled and a token configured.</summary>
        public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(UploadToken);

        /// <summary>The route as it appears in logs and the health report.</summary>
        public string Endpoint => $"{ListenUri.TrimEnd('/')}{ListenPath}";

        public static FuelUsageMonitorSettings FromConfiguration(IConfiguration? configuration)
        {
            bool enabled = bool.TryParse(configuration?["FuelUsageMonitor:Enabled"], out bool configuredEnabled) && configuredEnabled;

            string listenUri = configuration?["FuelUsageMonitor:ListenUri"]?.Trim() is { Length: > 0 } configuredUri
                ? configuredUri
                : DefaultListenUri;

            string listenPath = configuration?["FuelUsageMonitor:ListenPath"]?.Trim() is { Length: > 0 } configuredPath
                ? NormalizePath(configuredPath)
                : DefaultListenPath;

            int maxUploads = DefaultMaxUploadsPerMinute;
            if (int.TryParse(configuration?["FuelUsageMonitor:MaxUploadsPerMinute"], out int configuredMaxUploads) && configuredMaxUploads > 0)
                maxUploads = configuredMaxUploads;

            int maxQueryBytes = DefaultMaxQueryBytes;
            if (int.TryParse(configuration?["FuelUsageMonitor:MaxQueryBytes"], out int configuredMaxQueryBytes) && configuredMaxQueryBytes >= 128)
                maxQueryBytes = configuredMaxQueryBytes;

            int maxBodyBytes = DefaultMaxBodyBytes;
            if (int.TryParse(configuration?["FuelUsageMonitor:MaxBodyBytes"], out int configuredMaxBodyBytes) && configuredMaxBodyBytes >= 1024)
                maxBodyBytes = configuredMaxBodyBytes;

            return new FuelUsageMonitorSettings
            {
                Enabled = enabled,
                ListenUri = listenUri,
                ListenPath = listenPath,
                UploadToken = configuration?["FuelUsageMonitor:UploadToken"]?.Trim() ?? string.Empty,
                MaxUploadsPerMinute = maxUploads,
                MaxQueryBytes = maxQueryBytes,
                MaxBodyBytes = maxBodyBytes
            };
        }

        private static string NormalizePath(string configuredPath)
        {
            string trimmed = configuredPath.TrimEnd('/');
            return trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
        }
    }
}
