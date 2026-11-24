using HambotV2.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HambotV2.FuelUsageMonitor
{
    /// <summary>
    /// Runs the Torque upload receiver: the Kestrel host from <see cref="FuelUsageWebHost"/> over the
    /// shared <see cref="FuelUsageEndpoint"/>. Inactive (and silent) unless enabled with a token;
    /// the receive path only counts, because trip accumulation is the next phase.
    /// </summary>
    internal sealed class FuelUsageIngestionService : BackgroundService
    {
        private readonly FuelUsageMonitorSettings _settings;
        private readonly FuelUsageEndpoint _endpoint;
        private readonly FuelUsageIngestionStatus _status;
        private readonly ILogger<FuelUsageIngestionService> _logger;

        public FuelUsageIngestionService(
            FuelUsageMonitorSettings settings,
            FuelUsageRequestGate gate,
            FuelUsageIngestionStatus status,
            IMaintenanceMode maintenance,
            ILogger<FuelUsageIngestionService> logger)
        {
            _settings = settings;
            _status = status;
            _logger = logger;
            _endpoint = new FuelUsageEndpoint(settings, gate, status, maintenance, logger);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.IsActive)
            {
                string reason = _settings.Enabled
                    ? "FuelUsageMonitor:Enabled is true but no FuelUsageMonitor:UploadToken is configured"
                    : "FuelUsageMonitor:Enabled is false";
                _status.SetState($"disabled ({reason})");
                _logger.LogInformation(
                    "Fuel usage ingestion disabled: {Reason}. Configure FuelUsageMonitor:Enabled and FuelUsageMonitor:UploadToken to turn it on.",
                    reason);
                return;
            }

            FuelUsageWebHost host;
            try
            {
                host = await FuelUsageWebHost.StartAsync(_settings, _endpoint.HandleAsync, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _status.SetState($"failed ({ex.GetType().Name})");
                _logger.LogCritical(ex, "The fuel usage ingestion endpoint could not start; the rest of the bot keeps running.");
                return;
            }

            await using (host)
            {
                _status.SetState($"listening at {_settings.Endpoint}");
                _logger.LogInformation("Fuel usage ingestion listening at {Endpoint}", _settings.Endpoint);

                try
                {
                    await host.WaitForShutdownAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }
}
