using HambotV2.Maintenance;
using HambotV2.Watchdog;

namespace HambotV2.Scheduling
{
    /// <summary>
    /// The generic scheduled-message delivery engine, hosted once per message kind (reminders
    /// are the first: <c>ScheduledMessageDeliveryService&lt;ScheduledReminderMessage&gt;</c>).
    /// It sleeps until the earliest pending due time — bounded by a fallback poll, and woken by
    /// <see cref="IScheduledDeliverySignal"/> whenever a pending set changes — then hands every
    /// due message to the kind's sender and repeats. The engine itself holds no database state:
    /// persistence lives behind <see cref="IScheduledMessageSource{TMessage}"/>, transport
    /// behind <see cref="IScheduledMessageSender{TMessage}"/>, and all time comes from the
    /// injected <see cref="TimeProvider"/> (tests advance it via FakeTimeProvider).
    ///
    /// While maintenance mode is active (a backup restore is replacing the database, FR-98) the
    /// whole delivery pass is skipped, so a restore can never exhaust a retry budget and mark a
    /// message failed; a message that does exhaust its retries is additionally posted to the
    /// operator notification channel (FR-118).
    /// </summary>
    internal sealed class ScheduledMessageDeliveryService<TMessage> : BackgroundService
        where TMessage : IScheduledMessage
    {
        /// <summary>Bounded fallback poll: covers clock drift and missed wake-up signals; also the maintenance re-check cadence.</summary>
        internal static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(10);

        /// <summary>Delivery attempts per message before it is recorded failed.</summary>
        internal const int MaxDeliveryAttempts = 3;

        /// <summary>Backoff between delivery attempts; the injected clock advances it in tests.</summary>
        internal static readonly TimeSpan RetryBackoffDelay = TimeSpan.FromSeconds(2);

        /// <summary>Pause after an unexpectedly failed pass, so the loop never spins hot.</summary>
        internal static readonly TimeSpan ResyncDelay = TimeSpan.FromSeconds(1);

        private readonly IScheduledDeliverySignal _signal;
        private readonly IScheduledMessageSource<TMessage> _source;
        private readonly IScheduledMessageSender<TMessage> _sender;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<ScheduledMessageDeliveryService<TMessage>> _logger;
        private readonly IMaintenanceMode? _maintenanceMode;
        private readonly INotificationPublisher? _notificationPublisher;

        public ScheduledMessageDeliveryService(
            IScheduledDeliverySignal signal,
            IScheduledMessageSource<TMessage> source,
            IScheduledMessageSender<TMessage> sender,
            TimeProvider timeProvider,
            ILogger<ScheduledMessageDeliveryService<TMessage>> logger,
            IMaintenanceMode? maintenanceMode = null,
            INotificationPublisher? notificationPublisher = null)
        {
            _signal = signal;
            _source = source;
            _sender = sender;
            _timeProvider = timeProvider;
            _logger = logger;
            _maintenanceMode = maintenanceMode;
            _notificationPublisher = notificationPublisher;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Scheduled message delivery service started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Maintenance mode (FR-98): the pass is skipped entirely — deliveries defer
                    // until the restore is done, and nothing gets marked failed meanwhile.
                    if (_maintenanceMode?.IsActive == true)
                    {
                        await TryDelayAsync(FallbackPollInterval, stoppingToken);
                        continue;
                    }

                    await WaitForNextDueTimeAsync(stoppingToken);
                    await DeliverDueMessagesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown: pending messages stay persisted and are delivered on the next start.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled message delivery pass failed; retrying shortly.");
                    await TryDelayAsync(ResyncDelay, stoppingToken);
                }
            }

            _logger.LogInformation("Scheduled message delivery service stopped.");
        }

        /// <summary>
        /// Sleeps until the earliest pending message is due — or the bounded fallback poll, or a
        /// wake-up signal (a message scheduled or cancelled), or shutdown — whichever comes
        /// first.
        /// </summary>
        private async Task WaitForNextDueTimeAsync(CancellationToken cancellationToken)
        {
            // Snapshot the wake-up signal before reading the pending set: a message scheduled
            // right after the read still releases this wait, so no wake-up can be missed.
            Task pendingChanged = _signal.WaitForPendingSetChangeAsync(cancellationToken);

            DateTimeOffset? earliestDue = await _source.GetEarliestDueTimeAsync(cancellationToken);

            if (earliestDue is null)
            {
                // Nothing pending: sleep until the pending set changes or the host stops.
                await pendingChanged;
                return;
            }

            TimeSpan remaining = earliestDue.Value - _timeProvider.GetUtcNow();
            TimeSpan wait = remaining < TimeSpan.Zero
                ? TimeSpan.Zero
                : remaining > FallbackPollInterval ? FallbackPollInterval : remaining;

            // The wait goes through the injected clock so tests advance it; the wake-up signal
            // races it, and whichever completes first lets the loop recompute its plan.
            Task wakeUpOrDelay = await Task.WhenAny(pendingChanged, Task.Delay(wait, _timeProvider, cancellationToken));
            await wakeUpOrDelay;
        }

        /// <summary>
        /// Delivers every due message. Each one is isolated: a failure is retried a bounded number
        /// of times and then recorded failed and logged, and no message's failure stops the others
        /// or crashes the service.
        /// </summary>
        private async Task DeliverDueMessagesAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<TMessage> dueMessages = await _source.GetDueMessagesAsync(_timeProvider.GetUtcNow(), cancellationToken);

            foreach (TMessage message in dueMessages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool delivered = false;
                for (int attempt = 1; attempt <= MaxDeliveryAttempts && !cancellationToken.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        await _sender.SendAsync(message, cancellationToken);
                        delivered = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Delivery of {MessageIdentity} failed (attempt {Attempt} of {MaxAttempts}).",
                            message.LogIdentity, attempt, MaxDeliveryAttempts);

                        if (attempt < MaxDeliveryAttempts)
                            await TryDelayAsync(RetryBackoffDelay, cancellationToken);
                    }
                }

                // Shutdown mid-retry leaves the message pending (delivered on the next start)
                // instead of recording it failed.
                if (!delivered && cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                await RecordDeliveryOutcomeAsync(message, delivered, cancellationToken);
            }
        }

        /// <summary>
        /// Records the outcome through the source. A bookkeeping failure must not stop the rest
        /// of the batch — the message stays pending and is retried on a later pass.
        /// </summary>
        private async Task RecordDeliveryOutcomeAsync(TMessage message, bool delivered, CancellationToken cancellationToken)
        {
            try
            {
                if (delivered)
                    await _source.RecordDeliveredAsync(message, cancellationToken);
                else
                {
                    await _source.RecordFailedAsync(message, _timeProvider.GetUtcNow(), cancellationToken);
                    _logger.LogError("Delivery of {MessageIdentity} failed after {MaxAttempts} attempts; marked as failed.",
                        message.LogIdentity, MaxDeliveryAttempts);

                    // Retry-exhausted deliveries reach the operator notification channel (FR-118);
                    // the publisher rate-limits, collapses, and never throws back.
                    if (_notificationPublisher is not null)
                        await _notificationPublisher.PublishAsync(
                            $"A scheduled message delivery exhausted its {MaxDeliveryAttempts} attempts and was marked failed: {message.LogIdentity}.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record the delivery outcome of {MessageIdentity}.", message.LogIdentity);
            }
        }

        private async Task TryDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                // Through the injected clock, so tests advance the retry backoff too.
                await Task.Delay(delay, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The caller's loop observes the shutdown through its own token check.
            }
        }
    }
}
