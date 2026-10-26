using Discord;
using HambotV2.Connections;
using HambotV2.Scheduling;
using HambotV2.SlashCommands;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.ReminderCommands
{
    /// <summary>
    /// Cancels one of the invoking user's own pending reminders by its number. Numbers
    /// come from <c>!reminders</c>; unknown and foreign numbers get the same answer, so users
    /// learn nothing about other users' reminders. User-scoped, so it works in direct messages
    /// too.
    /// </summary>
    internal sealed class CancelReminderCommand : ICommand
    {
        private readonly IScheduledDeliverySignal _deliverySignal;

        public CancelReminderCommand(IScheduledDeliverySignal deliverySignal)
        {
            _deliverySignal = deliverySignal;
        }

        public string Name => "cancelreminder";
        public string Description => "Cancel one of your pending reminders by its number";
        public string Usage => "cancelreminder <number>";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        /// <summary>Slash surface: the reminder number picked as an integer.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand => new(
        [
            new ApplicationCommandOption("number", "The number of the reminder to cancel (from the reminders list)", ApplicationCommandOptionKind.Integer),
        ]);

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            var arguments = CommandArguments.Parse(message.Content);

            if (!arguments.TryGetInt(0, out int number))
            {
                await message.Channel.SendMessageAsync(
                    "Please give the number of the reminder to cancel, e.g. !cancelreminder 3 (numbers come from !reminders)");
                return;
            }

            // Own pending reminders only: the owner filter makes foreign numbers look
            // exactly like unknown ones.
            ulong ownerId = message.Author.Id;
            var reminder = await context.DbContext.Reminders
                .FirstOrDefaultAsync(item => item.OwnerDiscordId == ownerId && item.Number == number && item.FailedAtUtc == null);

            if (reminder is null)
            {
                await message.Channel.SendMessageAsync($"You have no pending reminder with number {number}");
                return;
            }

            context.DbContext.Reminders.Remove(reminder);
            await context.DbContext.SaveChangesAsync();

            // The cancelled reminder may have been the earliest: wake the delivery loop so it
            // re-schedules.
            _deliverySignal.NotifyPendingSetChanged();

            await message.Channel.SendMessageAsync($"Cancelled reminder #{number}");
        }
    }
}
