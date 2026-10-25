using HambotV2.Commands.QuoteCommands;
using HambotV2.Commands.ReminderCommands;
using HambotV2.Commands.ResponseCommands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Scheduling;

namespace HambotV2.Commands
{
    /// <summary>
    /// The head of the command registration chain; new command groups plug in here.
    /// </summary>
    internal static class RegisterAllCommands
    {
        public static void RegisterCommands(ICommandService commandService, IScheduledDeliverySignal deliverySignal)
        {
            RegisterSystemCommands.RegisterCommands(commandService);
            RegisterQuoteCommands.RegisterCommands(commandService);
            RegisterResponseCommands.RegisterCommands(commandService);
            RegisterReminderCommands.RegisterCommands(commandService, deliverySignal);
        }
    }
}
