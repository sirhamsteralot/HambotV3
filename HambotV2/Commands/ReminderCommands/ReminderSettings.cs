namespace HambotV2.Commands.ReminderCommands
{
    /// <summary>
    /// The recurring-reminder limits from the optional <c>Reminders</c> configuration section
    /// (§3.14): the minimum recurrence interval (<c>Reminders:MinRecurrenceMinutes</c>, default
    /// 10) and the separate per-user cap on pending recurring reminders
    /// (<c>Reminders:MaxRecurring</c>, default 5). A null or invalid entry falls back to the
    /// default, so a typo in configuration degrades to the shipped value instead of breaking
    /// the command — the same treatment the per-tier cooldown windows get (§3.4).
    /// </summary>
    internal sealed record ReminderSettings
    {
        internal const int DefaultMinRecurrenceMinutes = 10;
        internal const int DefaultMaxRecurring = 5;

        /// <summary>Shortest accepted recurrence interval (FR-107).</summary>
        public TimeSpan MinRecurrenceInterval { get; init; } = TimeSpan.FromMinutes(DefaultMinRecurrenceMinutes);

        /// <summary>Maximum pending recurring reminders per user (FR-109); 0 disables recurring reminders.</summary>
        public int MaxRecurring { get; init; } = DefaultMaxRecurring;

        public static ReminderSettings FromConfiguration(IConfiguration? configuration)
        {
            int minMinutes = DefaultMinRecurrenceMinutes;
            if (int.TryParse(configuration?["Reminders:MinRecurrenceMinutes"], out int configuredMin) && configuredMin > 0)
                minMinutes = configuredMin;

            int maxRecurring = DefaultMaxRecurring;
            if (int.TryParse(configuration?["Reminders:MaxRecurring"], out int configuredMax) && configuredMax >= 0)
                maxRecurring = configuredMax;

            return new ReminderSettings
            {
                MinRecurrenceInterval = TimeSpan.FromMinutes(minMinutes),
                MaxRecurring = maxRecurring
            };
        }
    }

    /// <summary>
    /// Human-readable recurrence intervals for replies and lists (FR-107/FR-108): the non-zero
    /// day/hour/minute/second components of the interval, largest first — e.g.
    /// <c>1d 12h</c>, <c>10m</c>. The parser-built intervals are sums of whole units, so this
    /// form always echoes what the user typed.
    /// </summary>
    internal static class ReminderIntervalFormat
    {
        public static string Format(TimeSpan interval)
        {
            var parts = new List<string>(4);
            if (interval.Days > 0)
                parts.Add($"{interval.Days}d");
            if (interval.Hours > 0)
                parts.Add($"{interval.Hours}h");
            if (interval.Minutes > 0)
                parts.Add($"{interval.Minutes}m");
            if (interval.Seconds > 0)
                parts.Add($"{interval.Seconds}s");

            return parts.Count > 0 ? string.Join(' ', parts) : "0s";
        }
    }
}
