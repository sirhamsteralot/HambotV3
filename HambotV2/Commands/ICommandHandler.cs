using Discord;

namespace HambotV2.Commands
{
    internal interface ICommandHandler : IDisposable
    {
        /// <summary>
        /// Runs the pipeline for one message and returns how it ended — the audit outcome,
        /// and for virtual invocations the public/ephemeral reply decision.
        /// </summary>
        Task<CommandOutcome> HandleCommand(IMessage message);
    }
}
