using HambotV2.Commands.QuoteCommands;
using HambotV2.Connections;

namespace HambotV2.QuoteOfTheDay
{
    /// <summary>
    /// One guild's resolved quote-of-the-day schedule: the target channel, the publication
    /// interval, and the UTC instant the schedule is anchored to. The boundaries are the anchor
    /// plus whole multiples of the interval — a daily interval publishes at the same UTC time
    /// every day. The record's value equality doubles as the publication loop's change detector:
    /// a changed schedule re-baselines the guild, so interval, anchor, and channel changes take
    /// effect at the next boundary of the new schedule and never trigger a mid-boundary
    /// publication.
    /// </summary>
    internal sealed record QuoteOfTheDaySchedule(
        ulong ChannelId,
        TimeSpan Interval,
        DateTimeOffset AnchorUtc)
    {
        internal const string DefaultTemplate = "quote of the day: {quote}";
        internal const string Placeholder = "{quote}";
        internal const int MaxTemplateLength = 500;
        internal const int DefaultIntervalMinutes = 24 * 60;

        /// <summary>The default anchor: 00:00 UTC, epoch-aligned so sub-day intervals land on round wall-clock times.</summary>
        internal static readonly DateTimeOffset DefaultAnchorUtc = DateTimeOffset.UnixEpoch;

        /// <summary>Reads the guild's stored configuration, applying the defaults for unset columns.</summary>
        internal static QuoteOfTheDaySchedule FromSetting(DatabaseContext.GuildSetting setting)
        {
            return new QuoteOfTheDaySchedule(
                setting.QuoteOfTheDayChannelId!.Value,
                TimeSpan.FromMinutes(setting.QuoteOfTheDayIntervalMinutes ?? DefaultIntervalMinutes),
                setting.QuoteOfTheDayAnchorUtc ?? DefaultAnchorUtc);
        }

        /// <summary>The stored or default template of the given guild's publication message.</summary>
        internal static string TemplateOf(DatabaseContext.GuildSetting setting)
        {
            return setting.QuoteOfTheDayTemplate ?? DefaultTemplate;
        }

        /// <summary>A template is valid when it is non-empty, at most 500 characters, and carries the placeholder.</summary>
        internal static bool IsValidTemplate(string template)
        {
            return template.Length > 0
                && template.Length <= MaxTemplateLength
                && template.Contains(Placeholder, StringComparison.Ordinal);
        }

        /// <summary>
        /// Renders the publication message: every placeholder occurrence carries the
        /// number-prefixed quote text, and the result is trimmed at Discord's message limit like
        /// the quote replies — the stored template and quote text are never modified.
        /// </summary>
        internal static string Render(string template, DatabaseContext.Quote quote)
        {
            return QuoteCommand.TrimToMessageLimit(template.Replace(Placeholder, QuoteCommand.FormatQuote(quote)));
        }

        /// <summary>
        /// The latest boundary (the anchor plus a whole multiple of the interval) at or before
        /// <paramref name="nowUtc"/> — a fixed schedule derived from the configuration, never
        /// from the last post.
        /// </summary>
        internal DateTimeOffset CurrentBoundary(DateTimeOffset nowUtc)
        {
            long steps = (long)Math.Floor((nowUtc - AnchorUtc).Ticks / (double)Interval.Ticks);
            return AnchorUtc + TimeSpan.FromTicks(steps * Interval.Ticks);
        }

        /// <summary>The first boundary strictly after <paramref name="nowUtc"/> — the guild's next due time.</summary>
        internal DateTimeOffset NextBoundary(DateTimeOffset nowUtc)
        {
            return CurrentBoundary(nowUtc) + Interval;
        }
    }
}
