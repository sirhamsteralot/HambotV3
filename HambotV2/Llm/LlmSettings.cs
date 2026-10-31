namespace HambotV2.Llm
{
    /// <summary>
    /// The optional <c>Llm</c> configuration section. A missing or invalid entry falls back to
    /// the shipped default — a typo degrades to the default instead of breaking startup. The API
    /// key is the only value that gates activity: an absent key (or a zero daily budget) means
    /// every LLM feature is inactive.
    /// </summary>
    internal sealed record LlmSettings
    {
        internal const string DefaultModel = "mistral-small-latest";
        internal const int DefaultIntervalMinutes = 60;
        internal const int DefaultMaxChatMessages = 20;
        internal const int DefaultMaxChatCharacters = 4000;
        internal const int DefaultDailyRequestBudget = 500;

        /// <summary>The Mistral API key (a secret — never in appsettings.json); null = inactive.</summary>
        public string? ApiKey { get; init; }

        public string Model { get; init; } = DefaultModel;

        /// <summary>The persona override from configuration; null = the shipped default seed.</summary>
        public string? Persona { get; init; }

        public bool PresenceUpdatesEnabled { get; init; }

        public int PresenceUpdatesIntervalMinutes { get; init; } = DefaultIntervalMinutes;

        public int ChatContextMaxMessages { get; init; } = DefaultMaxChatMessages;

        public int ChatContextMaxCharacters { get; init; } = DefaultMaxChatCharacters;

        public bool BioUpdatesEnabled { get; init; }

        public int BioUpdatesIntervalMinutes { get; init; } = DefaultIntervalMinutes;

        /// <summary>Per-UTC-day request hard stop; 0 disables the feature set like an absent key.</summary>
        public int DailyRequestBudget { get; init; } = DefaultDailyRequestBudget;

        /// <summary>True when an API key is configured and the daily budget is not zero.</summary>
        public bool Active => !string.IsNullOrWhiteSpace(ApiKey) && DailyRequestBudget > 0;

        public static LlmSettings FromConfiguration(IConfiguration? configuration)
        {
            string model = configuration?["Llm:Model"] is { Length: > 0 } configuredModel
                ? configuredModel
                : DefaultModel;

            int intervalMinutes = DefaultIntervalMinutes;
            if (int.TryParse(configuration?["Llm:PresenceUpdates:IntervalMinutes"], out int configuredInterval) && configuredInterval > 0)
                intervalMinutes = configuredInterval;

            int maxMessages = DefaultMaxChatMessages;
            if (int.TryParse(configuration?["Llm:ChatContext:MaxMessages"], out int configuredMessages) && configuredMessages > 0)
                maxMessages = configuredMessages;

            int bioIntervalMinutes = DefaultIntervalMinutes;
            if (int.TryParse(configuration?["Llm:BioUpdates:IntervalMinutes"], out int configuredBioInterval) && configuredBioInterval > 0)
                bioIntervalMinutes = configuredBioInterval;

            int maxCharacters = DefaultMaxChatCharacters;
            if (int.TryParse(configuration?["Llm:ChatContext:MaxCharacters"], out int configuredCharacters) && configuredCharacters > 0)
                maxCharacters = configuredCharacters;

            int budget = DefaultDailyRequestBudget;
            if (int.TryParse(configuration?["Llm:DailyRequestBudget"], out int configuredBudget) && configuredBudget >= 0)
                budget = configuredBudget;

            return new LlmSettings
            {
                ApiKey = configuration?["Llm:ApiKey"],
                Model = model,
                Persona = configuration?["Llm:Persona"],
                PresenceUpdatesEnabled = bool.TryParse(configuration?["Llm:PresenceUpdates:Enabled"], out bool presence) && presence,
                PresenceUpdatesIntervalMinutes = intervalMinutes,
                ChatContextMaxMessages = maxMessages,
                ChatContextMaxCharacters = maxCharacters,
                BioUpdatesEnabled = bool.TryParse(configuration?["Llm:BioUpdates:Enabled"], out bool bio) && bio,
                BioUpdatesIntervalMinutes = bioIntervalMinutes,
                DailyRequestBudget = budget
            };
        }
    }
}
