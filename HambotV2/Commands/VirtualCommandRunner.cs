using Discord;

namespace HambotV2.Commands
{
    /// <summary>
    /// One non-gateway invocation to run through the real command pipeline (design §3.15/§3.11):
    /// the invoking user, the channel it arrived on (a guild channel carries the guild scope,
    /// anything else runs in the DM scope), the timestamp (commands schedule from it),
    /// and the equivalent text command line — command name plus arguments, identifier excluded.
    /// </summary>
    /// <param name="Author">The invoking user, as reported by the origin surface.</param>
    /// <param name="OriginChannel">The origin channel; guild data and audit ids flow through, replies are captured.</param>
    /// <param name="CommandLine">The equivalent text command line without identifier, e.g. <c>userpermission &lt;@1&gt; 2</c>.</param>
    /// <param name="Timestamp">When the invocation happened; reminder scheduling uses it.</param>
    /// <param name="OperatorAgentUserId">The Discord id granted tier-4 authority for this invocation (the MCP run_command tool); null keeps the invoking user's real tiers.</param>
    internal sealed record VirtualCommandInvocation(IUser Author, IMessageChannel OriginChannel, string CommandLine, DateTimeOffset Timestamp, ulong? OperatorAgentUserId = null);

    /// <summary>
    /// One captured reply of a virtual invocation: the text, plus any message components the
    /// command attached (e.g. help's pagination buttons) so the origin interaction can re-post
    /// the reply intact.
    /// </summary>
    internal sealed record VirtualCommandReply(string Text, MessageComponent? Components);

    /// <summary>
    /// The captured result of a virtual invocation: the pipeline outcome (the audit outcome
    /// and reply-ephemerality input) and every reply the pipeline sent,
    /// in order, without posting anything to the origin channel.
    /// </summary>
    internal sealed record VirtualCommandResult(CommandOutcome Outcome, IReadOnlyList<VirtualCommandReply> Replies);

    internal interface IVirtualCommandRunner
    {
        /// <summary>Executes the invocation through the real per-message pipeline and captures its replies.</summary>
        Task<VirtualCommandResult> RunAsync(VirtualCommandInvocation invocation);
    }

    /// <summary>
    /// The shared virtual-invocation seam (design §3.11): resolves a transient command handler
    /// from the container exactly like the gateway path (one per invocation, disposed after
    /// use), prefixes the guild's effective identifier so the pipeline resolves the command
    /// exactly as for a text message, and captures the replies instead of posting them.
    /// The slash surface (§3.15) consumes it today; the MCP <c>run_command</c> tool reuses it.
    /// </summary>
    internal sealed class VirtualCommandRunner : IVirtualCommandRunner
    {
        private readonly IGuildSettingsService _guildSettingsService;
        private readonly IServiceProvider _serviceProvider;

        public VirtualCommandRunner(IGuildSettingsService guildSettingsService, IServiceProvider serviceProvider)
        {
            _guildSettingsService = guildSettingsService;
            _serviceProvider = serviceProvider;
        }

        public async Task<VirtualCommandResult> RunAsync(VirtualCommandInvocation invocation)
        {
            var channel = invocation.OriginChannel is IGuildChannel
                ? new VirtualGuildCommandChannel(invocation.OriginChannel)
                : new VirtualCommandChannel(invocation.OriginChannel);

            // The identifier is resolved with the same rules the pipeline applies to the
            // resulting message, so the built command line resolves back to the command.
            string identifier = invocation.OriginChannel is IGuildChannel guildChannel
                ? _guildSettingsService.GetCommandIdentifier(guildChannel.GuildId)
                : _guildSettingsService.GlobalCommandIdentifier;

            string content = string.IsNullOrWhiteSpace(invocation.CommandLine)
                ? identifier
                : $"{identifier}{invocation.CommandLine.TrimStart()}";

            var message = new VirtualCommandMessage(invocation.Author, channel, content, invocation.Timestamp, invocation.OperatorAgentUserId);

            using var handler = _serviceProvider.GetService<ICommandHandler>();
            if (handler is null)
                throw new InvalidOperationException("No command handler is registered to serve virtual invocations.");

            CommandOutcome outcome = await handler.HandleCommand(message);
            return new VirtualCommandResult(outcome, channel.SentMessages);
        }
    }
}
