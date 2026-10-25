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
        }
    }
}
