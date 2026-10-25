using HambotV2.Connections;
using HambotV2.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Reminders
{
    /// <summary>
    /// The reminder persistence behind the generic scheduled-message engine: the earliest
    /// pending due time, the due list, and the delivery outcomes. Delivered one-shot rows are
    /// deleted from the pending set; a delivered recurring row is re-scheduled from the actual
    /// delivery time (FR-107), so downtime shifts its schedule instead of piling up a burst of
    /// catch-up deliveries; rows that exhausted the bounded retries are marked
    /// <see cref="DatabaseContext.Reminder.FailedAtUtc"/> and stay for audit — a recurring row
    /// that keeps failing stops repeating like a one-shot (FR-108). All database work goes
    /// through fresh <see cref="IServiceScopeFactory"/> scopes, so the singleton never holds a
    /// DbContext.
    /// </summary>
    internal sealed class ReminderMessageSource : IScheduledMessageSource<ScheduledReminderMessage>
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<ReminderMessageSource> _logger;

        public ReminderMessageSource(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<ReminderMessageSource> logger)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<DateTimeOffset?> GetEarliestDueTimeAsync(CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            return await database.Reminders
                .Where(reminder => reminder.FailedAtUtc == null)
                .MinAsync(reminder => (DateTimeOffset?)reminder.DueAtUtc, cancellationToken);
        }

        public async Task<IReadOnlyList<ScheduledReminderMessage>> GetDueMessagesAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            List<ScheduledReminderMessage> due = await database.Reminders
                .Where(reminder => reminder.FailedAtUtc == null && reminder.DueAtUtc <= now)
                .OrderBy(reminder => reminder.DueAtUtc)
                .ThenBy(reminder => reminder.Number)
                .Select(reminder => new ScheduledReminderMessage(reminder.OwnerDiscordId, reminder.Number, reminder.Message, reminder.RecurrenceInterval))
                .ToListAsync(cancellationToken);

            return due;
        }

        public async Task RecordDeliveredAsync(ScheduledReminderMessage message, CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            var stored = await FindStoredReminderAsync(database, message, cancellationToken);
            if (stored is null)
            {
                // Cancelled while delivery was in flight; nothing to record.
                return;
            }

            if (stored.RecurrenceInterval is TimeSpan interval)
            {
                // Recurring row (FR-107): re-schedule from the actual delivery time instead of
                // deleting, so downtime shifts the schedule rather than bursting catch-ups. The
                // number and row persist, keeping the entry's identity stable (FR-108).
                stored.DueAtUtc = _timeProvider.GetUtcNow().Add(interval);
                await database.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Recurring reminder {ReminderNumber} delivered to user {OwnerDiscordId}; re-scheduled.",
                    stored.Number, stored.OwnerDiscordId);
                return;
            }

            database.Reminders.Remove(stored);
            await database.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Reminder {ReminderNumber} delivered to user {OwnerDiscordId}.",
                stored.Number, stored.OwnerDiscordId);
        }

        public async Task RecordFailedAsync(ScheduledReminderMessage message, DateTimeOffset failedAtUtc, CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            var stored = await FindStoredReminderAsync(database, message, cancellationToken);
            if (stored is null)
            {
                // Cancelled while delivery was in flight; nothing to record.
                return;
            }

            stored.FailedAtUtc = failedAtUtc;
            await database.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Loads the row as currently stored — within the caller's scope, so the outcome is
        /// recorded against the live row: a reminder cancelled (or already processed) while
        /// delivery was in flight is not resurrected.
        /// </summary>
        private static async Task<DatabaseContext.Reminder?> FindStoredReminderAsync(
            IDatabaseContext database, ScheduledReminderMessage message, CancellationToken cancellationToken)
        {
            return await database.Reminders.FindAsync(
                new object[] { message.OwnerDiscordId, message.Number }, cancellationToken);
        }
    }
}
