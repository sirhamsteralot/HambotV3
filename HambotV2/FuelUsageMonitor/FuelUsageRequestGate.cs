using HambotV2.Connections.Http;

namespace HambotV2.FuelUsageMonitor
{
    /// <summary>Why a request did not become a queued sample.</summary>
    internal enum FuelUsageOutcome
    {
        /// <summary>A sample was parsed and handed to the consumer.</summary>
        Sample,

        /// <summary>Authenticated and acknowledged, but the request carried no sample (a file upload, a Test settings ping).</summary>
        NotASample,

        Maintenance,
        RateLimited,
        QueryTooLarge,
        Unauthorized,
        BodyTooLarge
    }

    /// <summary>One Torque upload request, flattened to what the gate needs.</summary>
    internal sealed record FuelUsageRequest
    {
        /// <summary>The query parameters in arrival order, duplicates preserved.</summary>
        public required IReadOnlyList<KeyValuePair<string, string>> Query { get; init; }

        public string? Authorization { get; init; }

        /// <summary>The raw (undecoded) query string, which is what the size bound measures.</summary>
        public string RawQuery { get; init; } = string.Empty;

        public string RemoteAddress { get; init; } = string.Empty;

        public bool MaintenanceActive { get; init; }
    }

    /// <summary>The gate's verdict: the status to answer and, for a real sample, the sample itself.</summary>
    internal sealed record FuelUsageIngestDecision(int StatusCode, FuelUsageOutcome Outcome, TorqueSample? Sample)
    {
        public static FuelUsageIngestDecision Refuse(int statusCode, FuelUsageOutcome outcome) => new(statusCode, outcome, null);
    }

    /// <summary>
    /// The refusal chain in front of the queue, in this order: maintenance, per-address rate,
    /// query size, bearer token — then parsing. Pure except for the rate limiter, so the whole
    /// matrix is unit-testable without HTTP.
    /// </summary>
    internal sealed class FuelUsageRequestGate
    {
        internal const int StatusOk = 200;
        internal const int StatusUnauthorized = 401;
        internal const int StatusPayloadTooLarge = 413;
        internal const int StatusUriTooLong = 414;
        internal const int StatusTooManyRequests = 429;
        internal const int StatusServiceUnavailable = 503;

        private readonly FuelUsageMonitorSettings _settings;
        private readonly FuelUsageRateLimiter _rateLimiter;

        public FuelUsageRequestGate(FuelUsageMonitorSettings settings, FuelUsageRateLimiter rateLimiter)
        {
            _settings = settings;
            _rateLimiter = rateLimiter;
        }

        public FuelUsageIngestDecision Evaluate(FuelUsageRequest request)
        {
            if (request.MaintenanceActive)
                return FuelUsageIngestDecision.Refuse(StatusServiceUnavailable, FuelUsageOutcome.Maintenance);

            if (!_rateLimiter.TryAcquire(request.RemoteAddress))
                return FuelUsageIngestDecision.Refuse(StatusTooManyRequests, FuelUsageOutcome.RateLimited);

            if (request.RawQuery.Length > _settings.MaxQueryBytes)
                return FuelUsageIngestDecision.Refuse(StatusUriTooLong, FuelUsageOutcome.QueryTooLarge);

            if (!BearerAuthentication.IsValid(request.Authorization, _settings.UploadToken))
                return FuelUsageIngestDecision.Refuse(StatusUnauthorized, FuelUsageOutcome.Unauthorized);

            TorqueSample? sample = TorqueSampleParser.Parse(request.Query);
            return sample is null
                ? new FuelUsageIngestDecision(StatusOk, FuelUsageOutcome.NotASample, null)
                : new FuelUsageIngestDecision(StatusOk, FuelUsageOutcome.Sample, sample);
        }
    }
}
