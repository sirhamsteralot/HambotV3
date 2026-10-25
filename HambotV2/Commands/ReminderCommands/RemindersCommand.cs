using Discord;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.ReminderCommands
{
    /// <summary>
    /// Lists the invoking user's pending reminders — number, due time, message — soonest first,
    /// capped at 10 entries (FR-68). User-scoped, so it works in direct messages too (FR-71).
    /// </summary>
    internal sealed class RemindersCommand : ICommand
    {
        private const int ListDisplayCap = 10;

        public string Name => "reminders";
        public string Description => "List your pending reminders, soonest first";
        public string Usage => "reminders";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Every query filters by owner (FR-69): users only ever see their own reminders.
            ulong ownerId = message.Author.Id;

            var pending = await context.DbContext.Reminders
                .Where(reminder => reminder.OwnerDiscordId == ownerId && reminder.FailedAtUtc == null)
                .OrderBy(reminder => reminder.DueAtUtc)
                .ThenBy(reminder => reminder.Number)
                .Take(ListDisplayCap)
                .ToListAsync();

            if (pending.Count == 0)
            {
                await message.Channel.SendMessageAsync("You have no pending reminders — set one with !remindme <duration> <message...>");
                return;
            }

            IEnumerable<string> lines = pending.Select(reminder =>
                $"#{reminder.Number} — due {reminder.DueAtUtc:yyyy-MM-dd HH:mm} UTC — {reminder.Message}");

            await message.Channel.SendMessageAsync("Your pending reminders:\n" + string.Join('\n', lines));
        }
    }
}
