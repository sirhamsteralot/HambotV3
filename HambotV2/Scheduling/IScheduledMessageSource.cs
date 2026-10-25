namespace HambotV2.Scheduling
{
    /// <summary>
    /// The persistence behind the delivery engine for one scheduled-message kind: what is
    /// pending, what is due, and what became of a delivery. Implementations own their database
    /// access (fresh scopes per operation — they never hold a DbContext) and their outcome
    /// semantics (delete on delivery, mark failed, or anything a future kind needs).
    /// </summary>
    internal interface IScheduledMessageSource<TMessage> where TMessage : IScheduledMessage
    {
        /// <summary>The earliest pending due time, or null when nothing is pending.</summary>
        Task<DateTimeOffset?> GetEarliestDueTimeAsync(CancellationToken cancellationToken);

        /// <summary>Every message due at or before <paramref name="now"/>, earliest first.</summary>
        Task<IReadOnlyList<TMessage>> GetDueMessagesAsync(DateTimeOffset now, CancellationToken cancellationToken);

        /// <summary>Records a successful delivery — normally removing the message from the pending set.</summary>
        Task RecordDeliveredAsync(TMessage message, CancellationToken cancellationToken);

        /// <summary>Records a delivery that failed its bounded retries — normally marking it failed for audit.</summary>
        Task RecordFailedAsync(TMessage message, DateTimeOffset failedAtUtc, CancellationToken cancellationToken);
    }
}
