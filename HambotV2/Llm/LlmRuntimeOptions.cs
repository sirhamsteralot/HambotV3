namespace HambotV2.Llm
{
    /// <summary>
    /// The runtime (in-memory) LLM update-job intervals, initialized from the configured
    /// <see cref="LlmSettings"/> and changeable at runtime via <c>!llminterval</c>; a restart
    /// restores the configured values. The scheduled loops read the current value every tick.
    /// </summary>
    internal sealed class LlmRuntimeOptions
    {
        private readonly LlmSettings _configured;

        public LlmRuntimeOptions(LlmSettings configured)
        {
            _configured = configured;
            PresenceIntervalMinutes = configured.PresenceUpdatesIntervalMinutes;
            BioIntervalMinutes = configured.BioUpdatesIntervalMinutes;
        }

        public int PresenceIntervalMinutes { get; private set; }

        public int BioIntervalMinutes { get; private set; }

        public int GetInterval(LlmSurface surface) =>
            surface == LlmSurface.Presence ? PresenceIntervalMinutes : BioIntervalMinutes;

        public bool IsRuntimeOverride(LlmSurface surface) =>
            GetInterval(surface) != (surface == LlmSurface.Presence
                ? _configured.PresenceUpdatesIntervalMinutes
                : _configured.BioUpdatesIntervalMinutes);

        public void SetInterval(LlmSurface surface, int minutes)
        {
            if (surface == LlmSurface.Presence)
                PresenceIntervalMinutes = minutes;
            else
                BioIntervalMinutes = minutes;
        }

        public void ResetInterval(LlmSurface surface) =>
            SetInterval(surface, surface == LlmSurface.Presence
                ? _configured.PresenceUpdatesIntervalMinutes
                : _configured.BioUpdatesIntervalMinutes);
    }
}
