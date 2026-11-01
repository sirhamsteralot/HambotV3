using HambotV2.Maintenance;

namespace HambotV2.Scheduling
{
    /// <summary>
    /// The one hosted loop behind every fixed-interval periodic job (the watchdog §3.13 and the
    /// quote-of-the-day §3.17; the due-time delivery engine
    /// <see cref="ScheduledMessageDeliveryService{TMessage}"/> is a different shape and stays,
    /// and the LLM presence/bio loops follow the roadmap phase-#2 item). A job supplies a name,
    /// an interval supplier (re-read every tick, so a runtime change applies from the next tick)
    /// and the run callback; the first tick runs immediately. The host sleeps on the injected
    /// <see cref="TimeProvider"/>, capped by a bounded fallback poll so clock drift can never
    /// stall the loop, re-arms even when a tick fails (a failing job never spins hot), and never
    /// lets an exception crash it. While maintenance mode is active (when the job opts in) the
    /// run is paused and the loop re-checks at the fallback poll; the resume hook fires once when
    /// maintenance ends.
    /// </summary>
    internal sealed class PeriodicJobHost : BackgroundService
    {
        /// <summary>Bounded fallback poll: caps every sleep, so drift and maintenance transitions are noticed within this bound.</summary>
        internal static readonly TimeSpan DefaultFallbackPollInterval = TimeSpan.FromSeconds(10);

        private readonly string _name;
        private readonly Func<TimeSpan> _interval;
        private readonly Func<DateTimeOffset, CancellationToken, Task> _runAsync;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly IMaintenanceMode? _maintenanceMode;
        private readonly Func<Task>? _onMaintenanceResumed;
        private readonly TimeSpan _fallbackPollInterval;

        public PeriodicJobHost(
            string name,
            Func<TimeSpan> interval,
            Func<DateTimeOffset, CancellationToken, Task> runAsync,
            TimeProvider timeProvider,
            ILogger logger,
            IMaintenanceMode? maintenanceMode = null,
            Func<Task>? onMaintenanceResumed = null,
            TimeSpan? fallbackPollInterval = null)
        {
            _name = name;
            _interval = interval;
            _runAsync = runAsync;
            _timeProvider = timeProvider;
            _logger = logger;
            _maintenanceMode = maintenanceMode;
            _onMaintenanceResumed = onMaintenanceResumed;
            _fallbackPollInterval = fallbackPollInterval ?? DefaultFallbackPollInterval;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

        internal async Task RunAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Name} started (fallback poll {PollInterval}).", _name, _fallbackPollInterval);

            bool maintenanceWasActive = false;
            DateTimeOffset nextRunAt = _timeProvider.GetUtcNow();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Maintenance mode: the run is paused entirely and the loop re-checks at
                    // the fallback poll, so the end of a restore is noticed quickly.
                    if (_maintenanceMode?.IsActive == true)
                    {
                        maintenanceWasActive = true;
                        await TryDelayAsync(_fallbackPollInterval, stoppingToken);
                        continue;
                    }

                    if (maintenanceWasActive)
                    {
                        maintenanceWasActive = false;
                        if (_onMaintenanceResumed is not null)
                            await _onMaintenanceResumed();
                        nextRunAt = _timeProvider.GetUtcNow();
                    }

                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    if (now >= nextRunAt)
                    {
                        try
                        {
                            await _runAsync(now, stoppingToken);
                        }
                        finally
                        {
                            // Re-arm even when the tick failed or was cancelled, so a failing
                            // job waits a full interval instead of spinning hot.
                            nextRunAt = _timeProvider.GetUtcNow() + _interval();
                        }
                    }

                    TimeSpan remaining = nextRunAt - _timeProvider.GetUtcNow();
                    TimeSpan wait = remaining < TimeSpan.Zero
                        ? TimeSpan.Zero
                        : remaining > _fallbackPollInterval ? _fallbackPollInterval : remaining;
                    await TryDelayAsync(wait, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Name} tick failed; continuing at the next tick.", _name);
                }
            }

            _logger.LogInformation("{Name} stopped.", _name);
        }

        private async Task TryDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                // Through the injected clock, so tests advance it via FakeTimeProvider.
                await Task.Delay(delay, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The loop observes the shutdown through its own token check.
            }
        }
    }
}
