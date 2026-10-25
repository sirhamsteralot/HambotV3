using Discord;
using HambotV2.Connections;
using HambotV2.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.ReminderCommands
{
    /// <summary>
    /// Creates a personal timed reminder (FR-61–FR-64, FR-70): the leading duration tokens give
    /// the wait, the remainder is the reminder message, and the bot confirms in-channel echoing
    /// the message and the due time. The leading <c>every</c> token instead creates a recurring
    /// reminder (FR-107–FR-109): first due after the interval, re-scheduled after each delivery,
    /// with a configurable minimum interval and a separate per-user cap. Reminders are
    /// user-scoped, so the command works in both guild channels and direct messages (FR-71).
    /// </summary>
    internal sealed class RemindMeCommand : ICommand
    {
        /// <summary>Maximum reminder message length (FR-62).</summary>
        internal const int MaxMessageLength = 500;

        /// <summary>Maximum pending one-shot reminders per user (FR-70); recurring rows never consume these slots (FR-109).</summary>
        internal const int MaxPendingReminders = 10;

        /// <summary>The token that switches the invocation to the recurring form (FR-107).</summary>
        internal const string RecurrenceToken = "every";

        private readonly IScheduledDeliverySignal _deliverySignal;
        private readonly ReminderSettings _settings;

        public RemindMeCommand(IScheduledDeliverySignal deliverySignal, ReminderSettings? settings = null)
        {
            _deliverySignal = deliverySignal;
            _settings = settings ?? new ReminderSettings();
        }

        public string Name => "remindme";
        public string Description => "Set a personal reminder the bot sends you as a direct message when it is due";
        public string Usage => "remindme <duration> <message...> | remindme every <duration> <message...> (durations use s/m/h/d/w units, e.g. remindme 1d 12h water the plants, or remindme every 1d take vitamins for a recurring one)";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            var arguments = CommandArguments.Parse(message.Content);

            // The leading "every" token switches to the recurring form (FR-107): the interval
            // follows it, then the message. Accepted case-insensitively like the duration units.
            bool recurring = string.Equals(arguments.Get(0), RecurrenceToken, StringComparison.OrdinalIgnoreCase);
            int durationIndex = recurring ? 1 : 0;

            if (!ReminderDurationParser.TryParse(arguments, durationIndex, out TimeSpan duration, out int intervalTokens))
            {
                await message.Channel.SendMessageAsync(
                    "I could not read a positive duration from that. Usage: !remindme <duration> <message...> — " +
                    "e.g. !remindme 1d 12h water the plants, or !remindme every 1d take vitamins for a recurring one (units: s, m, h, d, w)");
                return;
            }

            string? reminderMessage = arguments.GetJoined(durationIndex + intervalTokens);
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

            if (recurring)
            {
                // The recurrence interval has a configurable floor (FR-107), enforced in-channel.
                if (duration < _settings.MinRecurrenceInterval)
                {
                    await message.Channel.SendMessageAsync(
                        $"Recurring reminders need an interval of at least {ReminderIntervalFormat.Format(_settings.MinRecurrenceInterval)}");
                    return;
                }

                // Separate per-user cap on pending recurring reminders (FR-109): recurring rows
                // never consume one-shot slots, so both caps are counted independently.
                int recurringCount = await context.DbContext.Reminders
                    .CountAsync(reminder => reminder.OwnerDiscordId == ownerId
                        && reminder.RecurrenceInterval != null
                        && reminder.FailedAtUtc == null);
                if (recurringCount >= _settings.MaxRecurring)
                {
                    await message.Channel.SendMessageAsync(
                        $"You already have {_settings.MaxRecurring} recurring reminders — cancel one with !cancelreminder before setting another");
                    return;
                }
            }
            else
            {
                // Per-user cap of pending one-shot reminders (FR-70); failed rows are not
                // pending, and recurring rows hold their own slots (FR-109).
                int pendingCount = await context.DbContext.Reminders
                    .CountAsync(reminder => reminder.OwnerDiscordId == ownerId
                        && reminder.RecurrenceInterval == null
                        && reminder.FailedAtUtc == null);
                if (pendingCount >= MaxPendingReminders)
                {
                    await message.Channel.SendMessageAsync(
                        $"You already have {MaxPendingReminders} pending reminders — cancel one with !cancelreminder before setting another");
                    return;
                }
            }

            // Sequential per-user numbering (FR-61), mirroring the quotes' per-guild numbering:
            // MAX(Number) + 1 within the owner, so cancelling the highest-numbered reminder
            // frees its number; cancellation never renumbers the remaining ones. Recurring rows
            // keep their number across deliveries because the row persists (FR-108).
            int nextNumber = (await context.DbContext.Reminders
                .Where(reminder => reminder.OwnerDiscordId == ownerId)
                .MaxAsync(reminder => (int?)reminder.Number) ?? -1) + 1;

            var reminderEntity = new DatabaseContext.Reminder
            {
                OwnerDiscordId = ownerId,
                Number = nextNumber,
                Message = reminderMessage,
                DueAtUtc = message.Timestamp.Add(duration),
                RecurrenceInterval = recurring ? duration : null,
                GuildId = context.GuildId,
                CreatedAt = message.Timestamp.UtcDateTime
            };

            context.DbContext.Reminders.Add(reminderEntity);
            await context.DbContext.SaveChangesAsync();

            // The new reminder may be the earliest pending one: wake the delivery loop so it
            // re-schedules instead of sleeping to the old horizon (FR-65).
            _deliverySignal.NotifyPendingSetChanged();

            string confirmation = recurring
                ? $"Recurring reminder #{nextNumber} set for {reminderEntity.DueAtUtc:yyyy-MM-dd HH:mm} UTC, repeating every {ReminderIntervalFormat.Format(duration)}: {reminderMessage}"
                : $"Reminder #{nextNumber} set for {reminderEntity.DueAtUtc:yyyy-MM-dd HH:mm} UTC: {reminderMessage}";
            await message.Channel.SendMessageAsync(confirmation);
        }
    }
}
