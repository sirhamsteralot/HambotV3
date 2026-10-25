using HambotV2.Scheduling;

namespace HambotV2.Commands.ReminderCommands
{
    internal static class RegisterReminderCommands
    {
        public static void RegisterCommands(ICommandService commandService, IScheduledDeliverySignal deliverySignal)
        {
            commandService.RegisterCommand("remindme", new RemindMeCommand(deliverySignal));
            commandService.RegisterCommand("reminders", new RemindersCommand());
            commandService.RegisterCommand("cancelreminder", new CancelReminderCommand(deliverySignal));
        }
    }
}
