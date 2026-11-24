using HambotV2.Maintenance;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace HambotV2.FuelUsageMonitor
{
    /// <summary>
    /// The request handler behind the route: builds the gate's view of the request, discards any
    /// uploaded body, answers <c>OK!</c> and counts the parsed sample. Nothing here touches the
    /// database, Discord or trip statistics — coordinates, PID values and the sender's email
    /// address stay in memory, and the counters keep neither.
    /// </summary>
    internal sealed class FuelUsageEndpoint
    {
        private const string SuccessBody = "OK!";
        private const int BodyBufferBytes = 8192;

        private readonly FuelUsageMonitorSettings _settings;
        private readonly FuelUsageRequestGate _gate;
        private readonly FuelUsageIngestionStatus _status;
        private readonly IMaintenanceMode _maintenance;
        private readonly ILogger _logger;

        public FuelUsageEndpoint(
            FuelUsageMonitorSettings settings,
            FuelUsageRequestGate gate,
            FuelUsageIngestionStatus status,
            IMaintenanceMode maintenance,
            ILogger logger)
        {
            _settings = settings;
            _gate = gate;
            _status = status;
            _maintenance = maintenance;
            _logger = logger;
        }

        public async Task HandleAsync(HttpContext context)
        {
            FuelUsageRequest request = BuildRequest(context);
            FuelUsageIngestDecision decision = _gate.Evaluate(request);

            if (decision.Outcome is not (FuelUsageOutcome.Sample or FuelUsageOutcome.NotASample))
            {
                _status.RecordRefused(decision.Outcome);
                _logger.LogDebug(
                    "Fuel usage upload {Outcome} ({StatusCode}), {ByteCount} bytes",
                    decision.Outcome,
                    decision.StatusCode,
                    request.RawQuery.Length);
                context.Response.StatusCode = decision.StatusCode;
                return;
            }

            long bodyBytes;
            try
            {
                bodyBytes = await DiscardBodyAsync(context);
            }
            catch (BadHttpRequestException)
            {
                _status.RecordRefused(FuelUsageOutcome.BodyTooLarge);
                _logger.LogDebug(
                    "Fuel usage upload {Outcome} ({StatusCode})",
                    FuelUsageOutcome.BodyTooLarge,
                    FuelUsageRequestGate.StatusPayloadTooLarge);
                context.Response.StatusCode = FuelUsageRequestGate.StatusPayloadTooLarge;
                return;
            }

            if (bodyBytes > 0)
                _status.RecordBody(bodyBytes);

            // The acknowledgement is written before the sample is counted: the app is waiting on
            // exactly this string, and nothing downstream may delay it.
            context.Response.StatusCode = FuelUsageRequestGate.StatusOk;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(SuccessBody, context.RequestAborted);

            if (decision.Sample is not { } sample)
            {
                _status.RecordRefused(FuelUsageOutcome.NotASample);
                return;
            }

            _status.RecordAccepted(sample);
            _logger.LogDebug(
                "Fuel usage sample accepted: {PidCount} PIDs ({PidKeys}), {BodyBytes} body bytes",
                sample.Values.Count,
                string.Join(",", sample.Values.Keys),
                bodyBytes);
        }

        /// <summary>
        /// Reads and throws away an uploaded body, counting bytes to the configured cap. No form
        /// binding, no multipart parsing, no temporary file: a Torque log upload costs a socket and
        /// nothing else, and past the cap the framework's own limit stops it.
        /// </summary>
        private async Task<long> DiscardBodyAsync(HttpContext context)
        {
            if (context.Request.ContentLength is 0)
                return 0;

            byte[] buffer = new byte[BodyBufferBytes];
            long total = 0;
            while (true)
            {
                int read = await context.Request.Body.ReadAsync(buffer.AsMemory(), context.RequestAborted);
                if (read == 0)
                    return total;

                total += read;
                if (total > _settings.MaxBodyBytes)
                    throw new BadHttpRequestException(
                        "Request body exceeds FuelUsageMonitor:MaxBodyBytes.",
                        FuelUsageRequestGate.StatusPayloadTooLarge);
            }
        }

        private FuelUsageRequest BuildRequest(HttpContext context)
        {
            var query = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, StringValues> parameter in context.Request.Query)
            {
                if (parameter.Value.Count == 0)
                {
                    query.Add(new KeyValuePair<string, string>(parameter.Key, string.Empty));
                    continue;
                }

                foreach (string? value in parameter.Value)
                    query.Add(new KeyValuePair<string, string>(parameter.Key, value ?? string.Empty));
            }

            return new FuelUsageRequest
            {
                Query = query,
                Authorization = context.Request.Headers.Authorization,
                RawQuery = context.Request.QueryString.Value ?? string.Empty,
                RemoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                MaintenanceActive = _maintenance.IsActive
            };
        }
    }
}
