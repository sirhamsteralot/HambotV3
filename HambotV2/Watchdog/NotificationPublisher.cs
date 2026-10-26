namespace HambotV2.Watchdog
{
    /// <summary>
    /// The single component posting operator notifications to the configured channel:
    /// watchdog alerts and recoveries, command execution failures, retry-exhausted
    /// reminder deliveries, maintenance-mode entry/exit and restore outcomes, and its own delivery
    /// failures (which additionally reach the bot owner by DM — the meta-failure the watchdog then
    /// tracks as "notification channel unreachable"). Posts are rate-limited
    /// (`Notifications:MaxPostsPerMinute`, default 5) with identical consecutive messages collapsed,
    /// so an error storm cannot flood the channel; the overflow stays fully present in the logs.
    /// Publishing never throws and never affects the originating feature.
    /// </summary>
    internal interface INotificationPublisher
    {
        /// <summary>Posts one operator notification; never throws (a failure is logged and, when possible, DM'd to the owner).</summary>
        Task PublishAsync(string message);

        /// <summary>True while the most recent channel post failed — the watchdog's channel-unreachable condition.</summary>
        bool ChannelDeliveryFailed { get; }
    }

    internal sealed class NotificationPublisher : INotificationPublisher
    {
        /// <summary>The rate-limit window: at most <c>MaxPostsPerMinute</c> posts per rolling minute.</summary>
        private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

        /// <summary>Default post cap per minute.</summary>
        internal const int DefaultMaxPostsPerMinute = 5;

        private readonly INotificationChannelStore _channelStore;
        private readonly IOperatorNotificationSender _sender;
        private readonly IConfiguration _configuration;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<NotificationPublisher> _logger;

        private readonly object _gate = new();
        private readonly Queue<DateTimeOffset> _recentPostTimes = new();
        private string? _lastPostedMessage;
        private bool _channelDeliveryFailed;

        public NotificationPublisher(
            INotificationChannelStore channelStore,
            IOperatorNotificationSender sender,
            IConfiguration configuration,
            TimeProvider timeProvider,
            ILogger<NotificationPublisher> logger)
        {
            _channelStore = channelStore;
            _sender = sender;
            _configuration = configuration;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public bool ChannelDeliveryFailed
        {
            get { lock (_gate) return _channelDeliveryFailed; }
        }

        public async Task PublishAsync(string message)
        {
            try
            {
                ulong? channelId = await _channelStore.GetChannelIdAsync();
                if (channelId is null)
                {
                    // No channel configured: notifications degrade to logs and owner DMs.
                    _logger.LogInformation("Operator notification (no channel configured, log only): {Message}", message);
                    return;
                }

                if (!TryReservePostingSlot(message))
                {
                    // Either identical to the previous post (collapse) or over the per-minute cap —
                    // both are the flood protection; the message is still in the logs.
                    return;
                }

                await _sender.SendToChannelAsync(channelId.Value, message);
                lock (_gate) _channelDeliveryFailed = false;
                _logger.LogInformation("Operator notification posted to channel {ChannelId}: {Message}", channelId.Value, message);
            }
            catch (Exception ex)
            {
                // A failed post is logged and never affects the originating feature; the
                // meta-failure additionally reaches the bot owner by DM and the watchdog via
                // ChannelDeliveryFailed.
                lock (_gate) _channelDeliveryFailed = true;
                _logger.LogError(ex, "Failed to post operator notification: {Message}", message);
                await TryNotifyOwnerOfDeliveryFailureAsync(message);
            }
        }

        /// <summary>
        /// Applies the flood protection: collapses a message identical to the previous post and
        /// enforces the per-minute cap. Returns false when the post should not be sent.
        /// </summary>
        private bool TryReservePostingSlot(string message)
        {
            int maxPostsPerMinute = ParseMaxPostsPerMinute();
            DateTimeOffset now = _timeProvider.GetUtcNow();

            lock (_gate)
            {
                if (string.Equals(_lastPostedMessage, message, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Operator notification collapsed (identical to the previous post): {Message}", message);
                    return false;
                }

                while (_recentPostTimes.Count > 0 && now - _recentPostTimes.Peek() >= RateLimitWindow)
                    _recentPostTimes.Dequeue();

                if (_recentPostTimes.Count >= maxPostsPerMinute)
                {
                    _logger.LogWarning("Operator notification dropped (rate limit of {MaxPosts} posts per minute reached): {Message}",
                        maxPostsPerMinute, message);
                    return false;
                }

                _recentPostTimes.Enqueue(now);
                _lastPostedMessage = message;
                return true;
            }
        }

        private async Task TryNotifyOwnerOfDeliveryFailureAsync(string message)
        {
            if (!ulong.TryParse(_configuration["Permissions:BotOwnerDiscordId"], out ulong ownerId))
                return;

            try
            {
                await _sender.SendOwnerDmAsync(ownerId,
                    $"A notification could not be posted to the operator channel: {message}");
            }
            catch (Exception dmEx)
            {
                _logger.LogError(dmEx, "Failed to DM the bot owner about a notification delivery failure.");
            }
        }

        private int ParseMaxPostsPerMinute()
        {
            return int.TryParse(_configuration["Notifications:MaxPostsPerMinute"], out int cap) && cap > 0
                ? cap
                : DefaultMaxPostsPerMinute;
        }
    }
}
