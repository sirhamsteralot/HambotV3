using System.Collections.Concurrent;

namespace HambotV2.LinkFix
{
    /// <summary>
    /// The Reddit video gate: a Reddit link is reposted only when its mirror's preview declares a video —
    /// that is what the rewrite buys for Reddit, since a text or image post unfurls fine on its own.
    /// </summary>
    internal sealed class LinkVideoProbe
    {
        /// <summary>Upper bound on cached verdicts; past it the expired entries go first, then the oldest one.</summary>
        internal const int MaxCacheEntries = 500;

        private readonly ILinkPreviewProbe _probe;
        private readonly LinkFixSettings _settings;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<LinkVideoProbe> _logger;
        private readonly ConcurrentDictionary<string, (PreviewVerdict Verdict, DateTimeOffset CheckedAt)> _verdicts = new();

        public LinkVideoProbe(
            ILinkPreviewProbe probe,
            LinkFixSettings settings,
            TimeProvider timeProvider,
            ILogger<LinkVideoProbe> logger)
        {
            _probe = probe;
            _settings = settings;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        /// <summary>True when the link may be reposted: its preview declares a video. A failed check means no.</summary>
        public async Task<bool> HasVideoAsync(string url)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_verdicts.TryGetValue(url, out (PreviewVerdict Verdict, DateTimeOffset CheckedAt) cached)
                && now - cached.CheckedAt < _settings.VideoProbeCacheLifetime)
            {
                return cached.Verdict == PreviewVerdict.Video;
            }

            PreviewVerdict verdict;
            try
            {
                verdict = await _probe.ReadVerdictAsync(url);
            }
            catch (Exception ex)
            {
                // Fail closed. The failure is logged by type only: an HttpRequestException's message
                // carries the URL, and no link may reach the log.
                _logger.LogWarning("Link preview check failed with {ExceptionType}; the link is not reposted.", ex.GetType().Name);
                return false;
            }

            // Only a conclusive answer is remembered: a page with no metadata, a "blocked" page, or a
            // truncated read never proves the post is not a video, so the next message asks again
            // instead of inheriting a silent outage for the whole cache lifetime.
            if (verdict != PreviewVerdict.NoMetadata)
                Remember(url, verdict, now);

            return verdict == PreviewVerdict.Video;
        }

        private void Remember(string url, PreviewVerdict verdict, DateTimeOffset now)
        {
            MakeRoom(now);
            _verdicts[url] = (verdict, now);
        }

        /// <summary>Keeps the cache bounded: drops what has expired, then the single oldest verdict.</summary>
        private void MakeRoom(DateTimeOffset now)
        {
            if (_verdicts.Count < MaxCacheEntries)
                return;

            string oldestUrl = string.Empty;
            DateTimeOffset oldestAt = DateTimeOffset.MaxValue;
            foreach (var entry in _verdicts)
            {
                if (now - entry.Value.CheckedAt >= _settings.VideoProbeCacheLifetime)
                {
                    _verdicts.TryRemove(entry.Key, out _);
                    continue;
                }

                if (entry.Value.CheckedAt < oldestAt)
                    (oldestUrl, oldestAt) = (entry.Key, entry.Value.CheckedAt);
            }

            if (_verdicts.Count >= MaxCacheEntries && oldestUrl.Length > 0)
                _verdicts.TryRemove(oldestUrl, out _);
        }
    }
}
