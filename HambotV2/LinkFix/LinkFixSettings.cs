namespace HambotV2.LinkFix
{
    /// <summary>
    /// The bounds from the optional <c>LinkFix</c> configuration section: distinct links per
    /// repost (<c>LinkFix:MaxLinksPerMessage</c>, default 5), the per-channel cooldown
    /// (<c>LinkFix:ChannelCooldownSeconds</c>, 0 = disabled), and the mapping cap
    /// (<c>LinkFix:MaxMappings</c>, default 50). A null or invalid entry falls back to the
    /// default, so a typo in configuration degrades to the shipped value instead of breaking the
    /// feature — the same treatment <c>ReminderSettings</c> gets. No key names a host: the
    /// origin→mirror table is runtime state (<c>!linkfixmap</c>).
    /// </summary>
    internal sealed record LinkFixSettings
    {
        internal const int DefaultMaxLinksPerMessage = 5;
        internal const int DefaultMaxMappings = 50;
        internal const int DefaultVideoProbeTimeoutSeconds = 5;
        internal const int DefaultVideoProbeCacheMinutes = 360;

        /// <summary>Distinct links per repost; the surplus is dropped from the tail.</summary>
        public int MaxLinksPerMessage { get; init; } = DefaultMaxLinksPerMessage;

        /// <summary>One repost per channel per window; zero or negative leaves the cooldown disabled.</summary>
        public TimeSpan ChannelCooldown { get; init; } = TimeSpan.Zero;

        /// <summary>Cap on the effective mapping size (shipped origins plus override-only origins).</summary>
        public int MaxMappings { get; init; } = DefaultMaxMappings;

        /// <summary>
        /// Gate Reddit reposts on the mirror's preview declaring a video: a Reddit post is only
        /// worth a repost when the fix buys a video embed. False reposts every eligible Reddit link
        /// without checking — the escape hatch for a mirror that serves no preview metadata.
        /// </summary>
        public bool RedditVideoProbe { get; init; } = true;

        /// <summary>Per-check HTTP timeout; a check that times out counts as no video (fail closed).</summary>
        public TimeSpan VideoProbeTimeout { get; init; } = TimeSpan.FromSeconds(DefaultVideoProbeTimeoutSeconds);

        /// <summary>How long a preview verdict is reused before the link is checked again.</summary>
        public TimeSpan VideoProbeCacheLifetime { get; init; } = TimeSpan.FromMinutes(DefaultVideoProbeCacheMinutes);

        public static LinkFixSettings FromConfiguration(IConfiguration? configuration)
        {
            int maxLinks = DefaultMaxLinksPerMessage;
            if (int.TryParse(configuration?["LinkFix:MaxLinksPerMessage"], out int configuredMaxLinks) && configuredMaxLinks > 0)
                maxLinks = configuredMaxLinks;

            int cooldownSeconds = 0;
            if (int.TryParse(configuration?["LinkFix:ChannelCooldownSeconds"], out int configuredCooldown) && configuredCooldown > 0)
                cooldownSeconds = configuredCooldown;

            int maxMappings = DefaultMaxMappings;
            if (int.TryParse(configuration?["LinkFix:MaxMappings"], out int configuredMaxMappings) && configuredMaxMappings > 0)
                maxMappings = configuredMaxMappings;

            bool redditVideoProbe = true;
            if (bool.TryParse(configuration?["LinkFix:RedditVideoProbe"], out bool configuredVideoProbe))
                redditVideoProbe = configuredVideoProbe;

            int probeTimeoutSeconds = DefaultVideoProbeTimeoutSeconds;
            if (int.TryParse(configuration?["LinkFix:VideoProbeTimeoutSeconds"], out int configuredProbeTimeout) && configuredProbeTimeout > 0)
                probeTimeoutSeconds = configuredProbeTimeout;

            int probeCacheMinutes = DefaultVideoProbeCacheMinutes;
            if (int.TryParse(configuration?["LinkFix:VideoProbeCacheMinutes"], out int configuredProbeCache) && configuredProbeCache > 0)
                probeCacheMinutes = configuredProbeCache;

            return new LinkFixSettings
            {
                MaxLinksPerMessage = maxLinks,
                ChannelCooldown = TimeSpan.FromSeconds(cooldownSeconds),
                MaxMappings = maxMappings,
                RedditVideoProbe = redditVideoProbe,
                VideoProbeTimeout = TimeSpan.FromSeconds(probeTimeoutSeconds),
                VideoProbeCacheLifetime = TimeSpan.FromMinutes(probeCacheMinutes)
            };
        }
    }
}
