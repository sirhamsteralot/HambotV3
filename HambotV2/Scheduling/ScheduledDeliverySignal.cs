namespace HambotV2.Scheduling
{
    /// <summary>
    /// Wake-up coordination between scheduled-message commands and the delivery loops: whenever
    /// a pending set changes (a message scheduled or cancelled), the commands signal it so the
    /// loops re-schedule when the change affects the earliest due time. One singleton instance
    /// is shared by all scheduled-message kinds; a notification wakes every loop, and each
    /// cheaply re-checks its own pending set.
    /// </summary>
    internal interface IScheduledDeliverySignal
    {
        /// <summary>
        /// Wakes the delivery loops. Safe to call at any time, including while nobody waits —
        /// a signal nobody observes is simply discarded.
        /// </summary>
        void NotifyPendingSetChanged();

        /// <summary>
        /// A task that completes on the next change notification, or throws when the token is
        /// cancelled. The delivery loop snapshots it before reading the pending set, so a change
        /// committed right after the read still wakes its wait (no missed wake-ups).
        /// </summary>
        Task WaitForPendingSetChangeAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// The signal behind <see cref="IScheduledDeliverySignal"/>: one replaceable
    /// <see cref="TaskCompletionSource"/> per change, so every waiter is released exactly once
    /// and a notification issued while the previous wake-up is still being processed lands on a
    /// fresh signal instead of vanishing.
    /// </summary>
    internal sealed class ScheduledDeliverySignal : IScheduledDeliverySignal
    {
        private volatile TaskCompletionSource _pendingSetChanged = CreateSignal();

        public void NotifyPendingSetChanged()
        {
            Interlocked.Exchange(ref _pendingSetChanged, CreateSignal()).TrySetResult();
        }

        public Task WaitForPendingSetChangeAsync(CancellationToken cancellationToken)
        {
            return _pendingSetChanged.Task.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource CreateSignal()
        {
            return new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
