namespace HambotV2.FuelUsageMonitor
{
    /// <summary>
    /// One Torque real-time upload sample: the drive keys, the sample instant, the sender's own
    /// labels, and every logged PID the request carried. Values are the app's own units — the
    /// receiver never converts, because it computes nothing yet.
    /// </summary>
    internal sealed record TorqueSample
    {
        /// <summary>The drive identifier (<c>session</c>); empty when the request only carried <c>id</c>.</summary>
        public string Session { get; init; } = string.Empty;

        /// <summary>The second drive identifier (<c>id</c>); empty when the request only carried <c>session</c>.</summary>
        public string DeviceId { get; init; } = string.Empty;

        /// <summary>The app's upload format version, stored verbatim and never assumed.</summary>
        public string Version { get; init; } = string.Empty;

        public DateTimeOffset TimestampUtc { get; init; }

        /// <summary>The app's User Email Address field, verbatim — the only sender discriminator it sends.</summary>
        public string Email { get; init; } = string.Empty;

        public string ProfileName { get; init; } = string.Empty;

        /// <summary>PID key to value, e.g. kd (speed) or kff1001 (GPS speed).</summary>
        public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>();
    }
}
