using Discord;

namespace HambotV2.Commands
{
    internal interface ICommandHandler : IDisposable
    {
        Task HandleCommand(IMessage message);
    }
}
