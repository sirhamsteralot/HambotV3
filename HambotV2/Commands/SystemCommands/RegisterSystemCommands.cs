using HambotV2.Watchdog;

namespace HambotV2.Commands.SystemCommands
{
    internal static class RegisterSystemCommands
    {
        public static void RegisterCommands(
            ICommandService commandService,
            INotificationChannelStore? notificationChannelStore = null,
            IHealthSnapshotProvider? healthSnapshotProvider = null)
        {
            commandService.RegisterCommand("hello", new HelloWorld());
            commandService.RegisterCommand("help", new HelpCommand(commandService, new PermissionsService()));
            commandService.RegisterCommand("commandpermission", new CommandPermission());
            commandService.RegisterCommand("userpermission", new UserPermission());
            commandService.RegisterCommand("settings", new SettingsCommand());
            commandService.RegisterCommand("version", new VersionCommand());
            commandService.RegisterCommand("notifychannel", new NotifyChannelCommand(notificationChannelStore));
            commandService.RegisterCommand("health", new HealthCommand(healthSnapshotProvider));
        }
    }
}
