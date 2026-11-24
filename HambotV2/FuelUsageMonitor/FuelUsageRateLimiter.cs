using System.Collections.Concurrent;

namespace HambotV2.FuelUsageMonitor
{
    /// <summary>
    /// Per-remote-address request bound for the upload endpoint: a fixed one-minute window that a
    /// phone sending one sample per second never reaches, and that stops a flood from reaching the
    /// parser. In memory only — a restart starts every window empty.
    /// </summary>
    internal sealed class FuelUsageRateLimiter
    {
        private const int PruneThreshold = 1024;
        private readonly int _limit;
        private readonly TimeProvider _timeProvider;
        private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

        public FuelUsageRateLimiter(int limit, TimeProvider timeProvider)
        {
            _limit = limit;
            _timeProvider = timeProvider;
        }

        public bool TryAcquire(string remoteAddress)
        {
            long minute = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / 60;
            Window window = _windows.GetOrAdd(remoteAddress, static _ => new Window());

            bool acquired;
            lock (window)
            {
                if (window.Minute != minute)
                {
                    window.Minute = minute;
                    window.Count = 0;
                }

                acquired = window.Count < _limit;
                if (acquired)
                    window.Count++;
            }

            if (_windows.Count > PruneThreshold)
                Prune(minute);

            return acquired;
        }

        private void Prune(long minute)
        {
            foreach (KeyValuePair<string, Window> entry in _windows)
            {
                bool stale;
                lock (entry.Value)
                    stale = entry.Value.Minute != minute;

                if (stale)
                    _windows.TryRemove(entry.Key, out _);
            }
        }

        private sealed class Window
        {
            public long Minute { get; set; } = -1;
            public int Count { get; set; }
        }
    }
}
