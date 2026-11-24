using System.Threading.Channels;
using HambotV2.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HambotV2.FuelUsageMonitor
{
    /// <summary>
    /// Runs the Torque upload receiver: the Kestrel host from <see cref="FuelUsageWebHost"/> plus the
    /// bounded handoff its samples arrive on. Inactive (and silent) unless enabled with a token;
    /// the consumer only counts, because trip accumulation is the next phase.
    /// </summary>
    internal sealed class FuelUsageIngestionService : BackgroundService
    {
        private readonly FuelUsageMonitorSettings _settings;
        private readonly FuelUsageEndpoint _endpoint;
        private readonly FuelUsageIngestionStatus _status;
        private readonly ILogger<FuelUsageIngestionService> _logger;
        private readonly Channel<TorqueSample> _queue;

        public FuelUsageIngestionService(
            FuelUsageMonitorSettings settings,
            FuelUsageRequestGate gate,
            FuelUsageIngestionStatus status,
            IMaintenanceMode maintenance,
            TimeProvider timeProvider,
            ILogger<FuelUsageIngestionService> logger)
        {
            _settings = settings;
            _status = status;
            _logger = logger;
            _queue = Channel.CreateBounded<TorqueSample>(new BoundedChannelOptions(settings.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });
            _endpoint = new FuelUsageEndpoint(settings, gate, status, _queue.Writer, maintenance, timeProvider, logger);
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
                    await ConsumeAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        /// <summary>
        /// Drains the queue. The trip accumulator of the next phase replaces this body; today a
        /// sample is counted and dropped, and nothing is persisted.
        /// </summary>
        private async Task ConsumeAsync(CancellationToken cancellationToken)
        {
            await foreach (TorqueSample sample in _queue.Reader.ReadAllAsync(cancellationToken))
                _status.RecordAccepted(sample);
        }
    }
}
