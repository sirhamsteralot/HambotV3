namespace HambotV2.Scheduling
{
    /// <summary>
    /// A pending scheduled message loaded by its <see cref="IScheduledMessageSource{TMessage}"/>.
    /// The delivery engine is generic over the message kind; each kind carries what the engine
    /// needs, and its sender knows the rest (recipient, transport, message text).
    /// </summary>
    internal interface IScheduledMessage
    {
        /// <summary>
        /// Log-safe identity without any message text — user text never reaches the logs —
        /// e.g. "reminder #3 of user 200".
        /// </summary>
        string LogIdentity { get; }
    }
}
