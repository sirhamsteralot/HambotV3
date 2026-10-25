namespace HambotV2.Scheduling
{
    /// <summary>
    /// The transport for one scheduled-message kind: sends a due message to its destination
    /// (direct message, channel, ...) and throws on failure so the engine's bounded retry
    /// applies.
    /// </summary>
    internal interface IScheduledMessageSender<TMessage> where TMessage : IScheduledMessage
    {
        Task SendAsync(TMessage message, CancellationToken cancellationToken);
    }
}
