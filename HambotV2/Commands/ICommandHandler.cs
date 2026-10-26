using Discord;

namespace HambotV2.Commands
{
    internal interface ICommandHandler : IDisposable
    {
        /// <summary>
        /// Runs the pipeline for one message and returns how it ended — the audit outcome
        /// (NFR-11) and, for virtual invocations, the public/ephemeral reply decision (FR-112).
        /// </summary>
        Task<CommandOutcome> HandleCommand(IMessage message);
    }
}
