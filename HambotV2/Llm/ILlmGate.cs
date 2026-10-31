namespace HambotV2.Llm
{
    /// <summary>Read-only gate state for <c>!llm status</c>, <c>!health</c>, and the watchdog.</summary>
    internal sealed record LlmStateSnapshot(
        bool Configured,
        bool KillSwitchActive,
        int RequestsToday,
        int DailyBudget,
        bool BudgetExhausted,
        int ConsecutiveFailures,
        IReadOnlyDictionary<string, LlmFeatureUsage> Features);

    /// <summary>
    /// The mandatory entry point for every LLM call: features never call <see cref="ILlmClient"/>
    /// directly. The gate checks the in-memory kill switch and the per-UTC-day request budget
    /// first, then calls the client and accounts the usage. A blocked or failed call returns
    /// null — a failed call is logged and counted, never thrown out.
    /// </summary>
    internal interface ILlmGate
    {
        /// <summary>True when a client is configured and the budget is not zero.</summary>
        bool IsConfigured { get; }

        bool KillSwitchActive { get; }

        bool BudgetExhausted { get; }

        void SetKillSwitch(bool active);

        /// <summary>Records an invalid model output like a failure (keep current state, retry next tick).</summary>
        void RecordInvalidOutput();

        Task<LlmResponse?> ChatAsync(string feature, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default);

        /// <summary>Lists the provider's model ids through the same guardrails (kill switch, budget); null when blocked or failed.</summary>
        Task<IReadOnlyList<string>?> ListModelsAsync(string feature, CancellationToken cancellationToken = default);

        LlmStateSnapshot Snapshot();
    }

    internal sealed class LlmGate : ILlmGate
    {
        private readonly ILlmClient? _client;
        private readonly ILlmUsageTracker _usage;
        private readonly LlmSettings _settings;
        private readonly ILogger<LlmGate> _logger;
        private bool _killSwitch;

        public LlmGate(ILlmClient? client, ILlmUsageTracker usage, LlmSettings settings, ILogger<LlmGate> logger)
        {
            _client = client;
            _usage = usage;
            _settings = settings;
            _logger = logger;
        }

        public bool IsConfigured => _client is not null && _settings.DailyRequestBudget > 0;

        public bool KillSwitchActive => _killSwitch;

        public bool BudgetExhausted => IsConfigured && _usage.RequestsToday >= _settings.DailyRequestBudget;

        public void SetKillSwitch(bool active)
        {
            _killSwitch = active;
        }

        public void RecordInvalidOutput() => _usage.RecordFailure();

        public Task<LlmResponse?> ChatAsync(string feature, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default) =>
            CallGuardedAsync(feature, "call", () => _client!.ChatAsync(messages, cancellationToken), response => (response.InputTokens, response.OutputTokens));

        public Task<IReadOnlyList<string>?> ListModelsAsync(string feature, CancellationToken cancellationToken = default) =>
            CallGuardedAsync(feature, "model list", () => _client!.ListModelsAsync(cancellationToken));

        /// <summary>Shared guardrails — configured, kill switch, daily budget — then the call with accounting. A blocked or failed call returns null: failures are logged and counted, never thrown out of the gate.</summary>
        private async Task<T?> CallGuardedAsync<T>(string feature, string action, Func<Task<T>> call, Func<T, (int Input, int Output)>? usageOf = null) where T : class
        {
            if (!IsConfigured)
                return null;

            if (_killSwitch)
            {
                _logger.LogInformation("LLM {Action} for '{Feature}' skipped: the kill switch is on.", action, feature);
                return null;
            }

            if (BudgetExhausted)
            {
                _logger.LogWarning("LLM {Action} for '{Feature}' skipped: the daily request budget ({Budget}) is exhausted.", action, feature, _settings.DailyRequestBudget);
                return null;
            }

            try
            {
                T result = await call();
                (int input, int output) = usageOf?.Invoke(result) ?? (0, 0);
                _usage.Record(feature, input, output);
                _usage.RecordSuccess();
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed call keeps the current state and counts toward the watchdog's
                // consecutive-failure alert; it never propagates out of the gate.
                _logger.LogError(ex, "LLM {Action} for '{Feature}' failed.", action, feature);
                _usage.RecordFailure();
                return null;
            }
        }

        public LlmStateSnapshot Snapshot() => new(
            IsConfigured,
            _killSwitch,
            _usage.RequestsToday,
            _settings.DailyRequestBudget,
            BudgetExhausted,
            _usage.ConsecutiveFailures,
            _usage.FeaturesToday);
    }
}
