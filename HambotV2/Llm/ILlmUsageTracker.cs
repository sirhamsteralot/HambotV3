namespace HambotV2.Llm
{
    /// <summary>One feature's usage for the current UTC day.</summary>
    internal sealed record LlmFeatureUsage(int Requests, int InputTokens, int OutputTokens);

    /// <summary>
    /// In-memory usage accounting: per feature per UTC day (requests, tokens in/out) plus the
    /// consecutive-failure counter the watchdog alerts on. The UTC day rolls over via the
    /// injected <see cref="TimeProvider"/>; a restart simply restores the configured state.
    /// </summary>
    internal interface ILlmUsageTracker
    {
        void Record(string feature, int inputTokens, int outputTokens);

        void RecordFailure();

        void RecordSuccess();

        int RequestsToday { get; }

        int ConsecutiveFailures { get; }

        IReadOnlyDictionary<string, LlmFeatureUsage> FeaturesToday { get; }
    }

    internal sealed class LlmUsageTracker : ILlmUsageTracker
    {
        private readonly TimeProvider _timeProvider;
        private readonly Dictionary<string, LlmFeatureUsage> _features = new();
        private DateOnly _day;
        private int _requestsToday;
        private int _consecutiveFailures;

        public LlmUsageTracker(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
            _day = CurrentDay();
        }

        public int RequestsToday { get { RollOver(); return _requestsToday; } }

        public int ConsecutiveFailures { get { RollOver(); return _consecutiveFailures; } }

        public IReadOnlyDictionary<string, LlmFeatureUsage> FeaturesToday { get { RollOver(); return _features; } }

        public void Record(string feature, int inputTokens, int outputTokens)
        {
            RollOver();
            if (!_features.TryGetValue(feature, out LlmFeatureUsage? usage))
                usage = new LlmFeatureUsage(0, 0, 0);
            _features[feature] = usage with
            {
                Requests = usage.Requests + 1,
                InputTokens = usage.InputTokens + inputTokens,
                OutputTokens = usage.OutputTokens + outputTokens
            };
            _requestsToday++;
        }

        public void RecordFailure()
        {
            RollOver();
            _consecutiveFailures++;
        }

        public void RecordSuccess()
        {
            RollOver();
            _consecutiveFailures = 0;
        }

        private DateOnly CurrentDay() => DateOnly.FromDateTime(_timeProvider.GetUtcNow().DateTime);

        private void RollOver()
        {
            DateOnly today = CurrentDay();
            if (today == _day)
                return;

            _day = today;
            _features.Clear();
            _requestsToday = 0;
            _consecutiveFailures = 0;
        }
    }
}
