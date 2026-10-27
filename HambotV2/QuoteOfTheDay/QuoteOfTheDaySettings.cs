namespace HambotV2.QuoteOfTheDay
{
    /// <summary>
    /// The deployment-level quote-of-the-day limit from the optional <c>QuoteOfTheDay</c>
    /// configuration section: the minimum publication interval
    /// (<c>QuoteOfTheDay:MinIntervalMinutes</c>, default 1) every guild's <c>every</c> setting is
    /// validated against. A null or invalid entry falls back to the default, so a typo in
    /// configuration degrades to the shipped value instead of breaking the command — the same
    /// treatment <c>ReminderSettings</c> gets.
    /// </summary>
    internal sealed record QuoteOfTheDaySettings
    {
        internal const int DefaultMinIntervalMinutes = 1;

        /// <summary>Shortest accepted publication interval.</summary>
        public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(DefaultMinIntervalMinutes);

        public static QuoteOfTheDaySettings FromConfiguration(IConfiguration? configuration)
        {
            int minMinutes = DefaultMinIntervalMinutes;
            if (int.TryParse(configuration?["QuoteOfTheDay:MinIntervalMinutes"], out int configuredMin) && configuredMin > 0)
                minMinutes = configuredMin;

            return new QuoteOfTheDaySettings { MinInterval = TimeSpan.FromMinutes(minMinutes) };
        }
    }
}
