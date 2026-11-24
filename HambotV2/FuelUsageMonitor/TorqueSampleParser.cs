using System.Globalization;

namespace HambotV2.FuelUsageMonitor
{
    /// <summary>
    /// Turns one Torque upload's flattened query parameters into a sample, or reports that the
    /// request is not a sample at all (the app's Test settings ping carries no data).
    /// Pure: the endpoint does the HTTP work, so every rule here is unit-testable.
    /// </summary>
    internal static class TorqueSampleParser
    {
        /// <summary>Bound on PID keys carried per sample; the surplus is dropped, never rejected.</summary>
        internal const int MaxPidCount = 128;

        internal const int MaxEmailLength = 320;

        internal const int MaxProfileNameLength = 128;

        internal const int MaxVersionLength = 16;

        /// <summary>2100-01-01T00:00:00Z — a sanity bound on the app's epoch-millisecond clock.</summary>
        private const long MaxTimestampMilliseconds = 4_102_444_800_000;

        public static TorqueSample? Parse(IReadOnlyList<KeyValuePair<string, string>> query)
        {
            string session = string.Empty;
            string deviceId = string.Empty;
            string version = string.Empty;
            string email = string.Empty;
            string profileName = string.Empty;
            string rawTime = string.Empty;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);

            // Last occurrence wins: a repeated key overwrites the earlier one, and later PID values
            // replace earlier values for the same PID.
            foreach (KeyValuePair<string, string> parameter in query)
            {
                switch (parameter.Key)
                {
                    case "session":
                        session = parameter.Value.Trim();
                        continue;
                    case "id":
                        deviceId = parameter.Value.Trim();
                        continue;
                    case "time":
                        rawTime = parameter.Value.Trim();
                        continue;
                    case "v":
                        version = Truncate(parameter.Value.Trim(), MaxVersionLength);
                        continue;
                    case "eml":
                        email = Truncate(parameter.Value.Trim(), MaxEmailLength);
                        continue;
                    case "profileName":
                        profileName = Truncate(parameter.Value.Trim(), MaxProfileNameLength);
                        continue;
                }

                if (!IsPidKey(parameter.Key))
                    continue;

                if (values.Count >= MaxPidCount && !values.ContainsKey(parameter.Key))
                    continue;

                if (!double.TryParse(parameter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || !double.IsFinite(value))
                    continue;

                values[parameter.Key] = value;
            }

            if (session.Length == 0 && deviceId.Length == 0)
                return null;

            if (!long.TryParse(rawTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestampMilliseconds)
                || timestampMilliseconds < 0
                || timestampMilliseconds > MaxTimestampMilliseconds)
                return null;

            return new TorqueSample
            {
                Session = session,
                DeviceId = deviceId,
                Version = version,
                TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds),
                Email = email,
                ProfileName = profileName,
                Values = values
            };
        }

        /// <summary>PID keys are the app's open-ended <c>k…</c> names (kc, kd, kff1001, a custom kff…).</summary>
        internal static bool IsPidKey(string key) => key.Length > 1 && key[0] == 'k';

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];
    }
}
