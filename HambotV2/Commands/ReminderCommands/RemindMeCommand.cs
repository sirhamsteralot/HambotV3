using Discord;
using HambotV2.Connections;
using HambotV2.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.ReminderCommands
{
    /// <summary>
    /// Creates a personal timed reminder (FR-61–FR-64, FR-70): the leading duration tokens give
    /// the wait, the remainder is the reminder message, and the bot confirms in-channel echoing
    /// the message and the due time. Reminders are user-scoped, so the command works in both
    /// guild channels and direct messages (FR-71).
    /// </summary>
    internal sealed class RemindMeCommand : ICommand
    {
        /// <summary>Maximum reminder message length (FR-62).</summary>
        internal const int MaxMessageLength = 500;

        /// <summary>Maximum pending reminders per user (FR-70).</summary>
        internal const int MaxPendingReminders = 10;

        private readonly IScheduledDeliverySignal _deliverySignal;

        public RemindMeCommand(IScheduledDeliverySignal deliverySignal)
        {
            _deliverySignal = deliverySignal;
        }

        public string Name => "remindme";
        public string Description => "Set a personal reminder the bot sends you as a direct message when it is due";
        public string Usage => "remindme <duration> <message...> (durations use s/m/h/d/w units, e.g. remindme 1d 12h water the plants)";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            var arguments = CommandArguments.Parse(message.Content);

            if (!ReminderDurationParser.TryParse(arguments, out TimeSpan duration, out int consumedTokens))
            {
                await message.Channel.SendMessageAsync(
                    "I could not read a positive duration from that. Usage: !remindme <duration> <message...> — " +
                    "e.g. !remindme 1d 12h water the plants (units: s, m, h, d, w)");
                return;
            }

            string? reminderMessage = arguments.GetJoined(consumedTokens);
            if (string.IsNullOrWhiteSpace(reminderMessage))
            {
                await message.Channel.SendMessageAsync("A reminder needs a message after the duration. Usage: !remindme <duration> <message...>");
                return;
            }

            if (reminderMessage.Length > MaxMessageLength)
            {
                await message.Channel.SendMessageAsync($"The reminder message must be at most {MaxMessageLength} characters long");
                return;
            }

            ulong ownerId = message.Author.Id;

            // Per-user cap of pending reminders (FR-70); failed rows are not pending.
            int pendingCount = await context.DbContext.Reminders
                .CountAsync(reminder => reminder.OwnerDiscordId == ownerId && reminder.FailedAtUtc == null);
            if (pendingCount >= MaxPendingReminders)
            {
                await message.Channel.SendMessageAsync(
                    $"You already have {MaxPendingReminders} pending reminders — cancel one with !cancelreminder before setting another");
                return;
            }

            // Sequential per-user numbering (FR-61), mirroring the quotes' per-guild numbering:
            // MAX(Number) + 1 within the owner, so cancelling the highest-numbered reminder
            // frees its number; cancellation never renumbers the remaining ones.
            int nextNumber = (await context.DbContext.Reminders
                .Where(reminder => reminder.OwnerDiscordId == ownerId)
                .MaxAsync(reminder => (int?)reminder.Number) ?? -1) + 1;

            var reminderEntity = new DatabaseContext.Reminder
            {
                OwnerDiscordId = ownerId,
                Number = nextNumber,
                Message = reminderMessage,
                DueAtUtc = message.Timestamp.Add(duration),
                GuildId = context.GuildId,
                CreatedAt = message.Timestamp.UtcDateTime
            };

            context.DbContext.Reminders.Add(reminderEntity);
            await context.DbContext.SaveChangesAsync();

            // The new reminder may be the earliest pending one: wake the delivery loop so it
            // re-schedules instead of sleeping to the old horizon (FR-65).
            _deliverySignal.NotifyPendingSetChanged();

            await message.Channel.SendMessageAsync(
                $"Reminder #{nextNumber} set for {reminderEntity.DueAtUtc:yyyy-MM-dd HH:mm} UTC: {reminderMessage}");
        }
    }
}
