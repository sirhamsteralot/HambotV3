using Discord;

namespace HambotV2.Commands.SystemCommands
{
    internal class HelloWorld : ICommand
    {
        public string Name => "hello";
        public string Description => "Greets the channel";
        public string Usage => "hello";
        public int DefaultPermissionTier => PermissionTier.Everyone;
        public CommandCategory Category => CommandCategory.General;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            await message.Channel.SendMessageAsync("Hello World");
        }
    }
}
