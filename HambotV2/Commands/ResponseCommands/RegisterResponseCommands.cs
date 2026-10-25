namespace HambotV2.Commands.ResponseCommands
{
    internal static class RegisterResponseCommands
    {
        public static void RegisterCommands(ICommandService commandService)
        {
            commandService.RegisterCommand("addresponse", new AddResponseCommand(commandService));
            commandService.RegisterCommand("removeresponse", new RemoveResponseCommand());
            commandService.RegisterCommand("listresponses", new ListResponsesCommand());
        }
    }
}
