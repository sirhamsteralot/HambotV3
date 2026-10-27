using System.Globalization;
using System.Text.RegularExpressions;

namespace HambotV2.Commands.ReminderCommands
{
    /// <summary>
    /// The shared duration parser for the duration-taking commands (<c>remindme</c>'s one-shot and
    /// recurring forms, the quote-of-the-day's due time and interval):
    /// number+unit tokens in compact (<c>10d</c>) and spaced (<c>10 d</c>) form — units
    /// <c>s</c>/<c>m</c>/<c>h</c>/<c>d</c>/<c>w</c> accepted case-insensitively, with common
    /// abbreviations (<c>sec</c>, <c>min</c>, <c>hr</c>, <c>wk</c>, and their plurals) and long
    /// forms (<c>second</c> … <c>week</c>) — with multiple tokens summed. A pure helper with no
    /// Discord or database surface, so it is directly unit-testable: one shared
    /// parser instead of per-command parsing).
    /// </summary>
    internal static partial class ReminderDurationParser
    {
        [GeneratedRegex(@"^(\d+)([a-zA-Z]+)$")]
        private static partial Regex CompactDurationToken();

        /// <summary>
        /// Tries to parse the duration from the leading arguments. On success,
        /// <paramref name="consumedTokens"/> is the number of leading arguments that carried the
        /// duration — the reminder message starts at that index — and the total is positive.
        /// The first argument that is not a duration token ends the duration; anything
        /// unparsable at position 0 makes the whole parse fail.
        /// </summary>
        public static bool TryParse(CommandArguments arguments, out TimeSpan duration, out int consumedTokens)
        {
            return TryParse(arguments, 0, out duration, out consumedTokens);
        }

        /// <summary>
        /// Parses exactly one duration token (compact <c>10d</c> or spaced <c>10 d</c>) without
        /// summing what follows — the single-duration form a command needs when a second duration
        /// comes after it, e.g. the quote-of-the-day's <c>&lt;due time&gt; &lt;interval&gt;</c>
        /// arguments. The total is positive; <paramref name="consumedTokens"/> counts from the
        /// arguments' start.
        /// </summary>
        public static bool TryParseFirst(CommandArguments arguments, int startIndex, out TimeSpan duration, out int consumedTokens)
        {
            duration = TimeSpan.Zero;
            consumedTokens = 0;

            if (!TryParseDurationToken(arguments, startIndex, out double seconds, out int tokens)
                || seconds <= 0
                || seconds > TimeSpan.MaxValue.TotalSeconds)
                return false;

            duration = TimeSpan.FromSeconds(seconds);
            consumedTokens = tokens;
            return true;
        }

        /// <summary>
        /// Same parse starting at <paramref name="startIndex"/> — the recurring form's interval
        /// begins after the leading <c>every</c> token. <paramref name="consumedTokens"/>
        /// counts from the arguments' start, so the message still starts at
        /// <c><paramref name="startIndex"/> + consumedTokens</c>.
        /// </summary>
        public static bool TryParse(CommandArguments arguments, int startIndex, out TimeSpan duration, out int consumedTokens)
        {
            duration = TimeSpan.Zero;
            consumedTokens = 0;

            double totalSeconds = 0;
            while (startIndex + consumedTokens < arguments.Count
                && TryParseDurationToken(arguments, startIndex + consumedTokens, out double tokenSeconds, out int tokensConsumed))
            {
                totalSeconds += tokenSeconds;
                consumedTokens += tokensConsumed;
            }

            // No maximum by requirement — but the TimeSpan type itself caps at ~29,000
            // years; anything beyond that is treated as unparsable instead of overflowing.
            if (consumedTokens == 0 || totalSeconds <= 0 || totalSeconds > TimeSpan.MaxValue.TotalSeconds)
            {
                consumedTokens = 0;
                return false;
            }

            duration = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }

        /// <summary>Parses one duration token: compact (<c>10d</c>) or spaced (<c>10 d</c>) form.</summary>
        private static bool TryParseDurationToken(CommandArguments arguments, int index, out double seconds, out int consumedTokens)
        {
            seconds = 0;
            consumedTokens = 0;

            string? token = arguments.Get(index);
            if (token is null)
                return false;

            Match compact = CompactDurationToken().Match(token);
            if (compact.Success)
            {
                if (!TryParseCount(compact.Groups[1].Value, out long count) || !TryParseUnit(compact.Groups[2].Value, out double unitSeconds))
                    return false;

                seconds = count * unitSeconds;
                consumedTokens = 1;
                return true;
            }

            // Spaced form: a bare number followed by a unit token.
            if (TryParseCount(token, out long spacedCount)
                && arguments.Get(index + 1) is string unitToken
                && TryParseUnit(unitToken, out double spacedUnitSeconds))
            {
                seconds = spacedCount * spacedUnitSeconds;
                consumedTokens = 2;
                return true;
            }

            return false;
        }

        private static bool TryParseCount(string digits, out long count)
        {
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out count);
        }

        private static bool TryParseUnit(string unit, out double seconds)
        {
            switch (unit.ToLowerInvariant())
            {
                case "s": case "sec": case "secs": case "second": case "seconds":
                    seconds = 1;
                    return true;
                case "m": case "min": case "mins": case "minute": case "minutes":
                    seconds = 60;
                    return true;
                case "h": case "hr": case "hrs": case "hour": case "hours":
                    seconds = 3600;
                    return true;
                case "d": case "day": case "days":
                    seconds = 86400;
                    return true;
                case "w": case "wk": case "wks": case "week": case "weeks":
                    seconds = 604800;
                    return true;
                default:
                    seconds = 0;
                    return false;
            }
        }
    }
}
