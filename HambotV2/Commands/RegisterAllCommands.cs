using HambotV2.Commands.QuoteCommands;
using HambotV2.Commands.ResponseCommands;
using HambotV2.Commands.SystemCommands;

namespace HambotV2.Commands
{
    /// <summary>
    /// The head of the command registration chain; new command groups plug in here.
    /// </summary>
    internal static class RegisterAllCommands
    {
        public static void RegisterCommands(ICommandService commandService)
        {
            RegisterSystemCommands.RegisterCommands(commandService);
            RegisterQuoteCommands.RegisterCommands(commandService);
            RegisterResponseCommands.RegisterCommands(commandService);
        }
    }
}
