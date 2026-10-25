namespace HambotV2.Commands.SystemCommands
{
    internal static class RegisterSystemCommands
    {
        public static void RegisterCommands(ICommandService commandService)
        {
            commandService.RegisterCommand("hello", new HelloWorld());
            commandService.RegisterCommand("help", new HelpCommand(commandService, new PermissionsService()));
            commandService.RegisterCommand("commandpermission", new CommandPermission());
            commandService.RegisterCommand("userpermission", new UserPermission());
            commandService.RegisterCommand("settings", new SettingsCommand());
        }
    }
}
