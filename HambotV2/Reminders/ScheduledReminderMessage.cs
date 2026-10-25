using HambotV2.Scheduling;

namespace HambotV2.Reminders
{
    /// <summary>
    /// A pending reminder loaded for delivery by the generic scheduled-message engine: the
    /// composite key (owner, number) identifies the stored row when the delivery outcome is
    /// recorded, the owner is the DM recipient, and the message text is delivered verbatim.
    /// </summary>
    internal sealed record ScheduledReminderMessage(ulong OwnerDiscordId, int Number, string Message) : IScheduledMessage
    {
        public string LogIdentity => $"reminder #{Number} of user {OwnerDiscordId}";
    }
}
