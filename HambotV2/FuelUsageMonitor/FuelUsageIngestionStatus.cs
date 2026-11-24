using System.Collections.Concurrent;
using System.Globalization;
using HambotV2.Backups;

namespace HambotV2.FuelUsageMonitor
{
    /// <summary>The endpoint's operator-facing state for the health snapshot.</summary>
    internal interface IFuelUsageIngestionStatus
    {
        string Describe();
    }

    /// <summary>
    /// The receive path's in-memory counters and endpoint state: what arrived, what was refused,
    /// what was dropped, and which PID keys the app actually logs. Nothing here is persisted, and
    /// no email address, coordinate or PID value is ever recorded — the row names keys and counts
    /// only, because a Torque URL carries a live position and the health report is shown in chat.
    /// </summary>
    internal sealed class FuelUsageIngestionStatus : IFuelUsageIngestionStatus
    {
        private const int MaxRememberedPidKeys = 32;

        private readonly ConcurrentDictionary<string, byte> _pidKeys = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<FuelUsageOutcome, int> _refusals = new();
        private long _accepted;
        private long _dropped;
        private long _bodies;
        private long _bodyBytes;
        private long _lastSampleMilliseconds;
        private string _lastSession = string.Empty;
        private volatile string _state = "not started";

        public string State => _state;

        public void SetState(string state) => _state = state;

        public void RecordAccepted(TorqueSample sample)
        {
            Interlocked.Increment(ref _accepted);
            Interlocked.Exchange(ref _lastSampleMilliseconds, sample.TimestampUtc.ToUnixTimeMilliseconds());
            _lastSession = sample.Session.Length > 0 ? sample.Session : sample.DeviceId;

            foreach (string pidKey in sample.Values.Keys)
            {
                if (_pidKeys.Count >= MaxRememberedPidKeys)
                    break;

                _pidKeys.TryAdd(pidKey, 0);
            }
        }

        public void RecordRefused(FuelUsageOutcome outcome) => _refusals.AddOrUpdate(outcome, 1, static (_, count) => count + 1);

        public void RecordDropped() => Interlocked.Increment(ref _dropped);

        public void RecordBody(long byteCount)
        {
            Interlocked.Increment(ref _bodies);
            Interlocked.Add(ref _bodyBytes, byteCount);
        }

        public string Describe()
        {
            // Per-reason counts are what tells an operator whether the app is misconfigured (401s),
            // probing the wrong path (404s), or simply uploading finished logs (not-a-sample).
            int refused = 0;
            var reasons = new List<string>();
            foreach (KeyValuePair<FuelUsageOutcome, int> entry in _refusals.OrderBy(static entry => entry.Key))
            {
                refused += entry.Value;
                reasons.Add($"{DescribeOutcome(entry.Key)} {entry.Value}");
            }

            long lastSample = Interlocked.Read(ref _lastSampleMilliseconds);
            string last = lastSample > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastSample).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : "never";
            string session = _lastSession.Length > 0 ? _lastSession : "none";
            string pidKeys = _pidKeys.Count > 0 ? string.Join(", ", _pidKeys.Keys.OrderBy(static key => key, StringComparer.Ordinal)) : "none";

            return $"{_state}"
                + $" · accepted {Interlocked.Read(ref _accepted)}"
                + $" · rejected {refused}" + (reasons.Count > 0 ? $" ({string.Join(", ", reasons)})" : string.Empty)
                + $" · dropped {Interlocked.Read(ref _dropped)}"
                + $" · bodies {Interlocked.Read(ref _bodies)} ({BackupSize.Format(Interlocked.Read(ref _bodyBytes))})"
                + $" · last {last}"
                + $" · session {session}"
                + $" · PIDs {pidKeys}";
        }

        private static string DescribeOutcome(FuelUsageOutcome outcome) => outcome switch
        {
            FuelUsageOutcome.NotASample => "not-a-sample",
            FuelUsageOutcome.QueryTooLarge => "query-too-large",
            FuelUsageOutcome.BodyTooLarge => "body-too-large",
            FuelUsageOutcome.RateLimited => "rate-limited",
            FuelUsageOutcome.Unauthorized => "unauthorized",
            FuelUsageOutcome.Maintenance => "maintenance",
            _ => outcome.ToString().ToLowerInvariant()
        };
    }
}
