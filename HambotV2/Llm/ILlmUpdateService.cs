namespace HambotV2.Llm
{
    /// <summary>The result of one LLM-driven surface update (scheduled or manually triggered).</summary>
    internal enum LlmUpdateOutcome
    {
        /// <summary>No API key or a zero budget — no call was attempted.</summary>
        Inactive,
        /// <summary>The kill switch is on or the daily budget is exhausted — no call was attempted.</summary>
        Blocked,
        /// <summary>The call errored — the current state was kept.</summary>
        Failed,
        /// <summary>The model's reply failed validation — the current state was kept.</summary>
        InvalidOutput,
        /// <summary>The validated output was applied to the surface.</summary>
        Applied
    }

    /// <summary>
    /// Manual entry point into the LLM update loop (the <c>!llm update</c> command). The same
    /// instance also runs the scheduled jobs, so manual and scheduled runs are serialized.
    /// </summary>
    internal interface ILlmUpdateService
    {
        /// <summary>Runs one update for the surface now, regardless of the scheduled-job flags; still goes through the gate.</summary>
        Task<LlmUpdateOutcome> UpdateAsync(LlmSurface surface, CancellationToken cancellationToken = default);
    }
}
