using HambotV2.Scheduling;

namespace HambotV2.Reminders
{
    /// <summary>
    /// A pending reminder loaded for delivery by the generic scheduled-message engine: the
    /// composite key (owner, number) identifies the stored row when the delivery outcome is
    /// recorded, the owner is the DM recipient, and the message text is delivered verbatim.
    /// A non-null <see cref="RecurrenceInterval"/> marks a recurring row (FR-107): the source
    /// re-schedules it after delivery instead of deleting it.
    /// </summary>
    internal sealed record ScheduledReminderMessage(
        ulong OwnerDiscordId,
        int Number,
        string Message,
        TimeSpan? RecurrenceInterval = null) : IScheduledMessage
    {
        public string LogIdentity => $"reminder #{Number} of user {OwnerDiscordId}";
    }
}
