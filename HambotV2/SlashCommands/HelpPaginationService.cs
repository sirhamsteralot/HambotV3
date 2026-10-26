using Discord;
using Discord.WebSocket;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;

namespace HambotV2.SlashCommands
{
    /// <summary>
    /// Interactive help pagination (design §3.15): a button click on a multi-page help list
    /// becomes a normal <c>help &lt;page&gt;</c> invocation through the same virtual-invocation
    /// seam the slash surface uses — so paging shares the command pipeline's permission, cooldown,
    /// and audit semantics — and the paginated message is then updated in place with the new page.
    /// Attached unconditionally: the buttons ride on text-surface help messages too, so paging
    /// works regardless of <c>SlashCommands:Enabled</c>.
    /// </summary>
    internal sealed class HelpPaginationService
    {
        private readonly DiscordSocketClient _client;
        private readonly IVirtualCommandRunner _virtualCommandRunner;
        private readonly ILogger<HelpPaginationService> _logger;

        public HelpPaginationService(DiscordSocketClient client, IVirtualCommandRunner virtualCommandRunner, ILogger<HelpPaginationService> logger)
        {
            _client = client;
            _virtualCommandRunner = virtualCommandRunner;
            _logger = logger;
        }

        /// <summary>Subscribes the help-button handler to the shared gateway client.</summary>
        public void Attach()
        {
            _client.ButtonExecuted += OnButtonExecutedAsync;
        }

        private async Task OnButtonExecutedAsync(SocketMessageComponent component)
        {
            if (!HelpPagination.TryGetTargetPage(component.Data.CustomId, out int targetPage))
                return;

            try
            {
                // Acknowledge within the interaction window; the update below resolves the deferred state.
                await component.DeferAsync();

                var invocation = new VirtualCommandInvocation(component.User, component.Channel, $"help {targetPage}", component.CreatedAt);
                VirtualCommandResult result = await _virtualCommandRunner.RunAsync(invocation);

                if (result.Outcome != CommandOutcome.Executed)
                {
                    // The pipeline declined the paging invocation (permission, cooldown, maintenance):
                    // like the slash surface, the denial is ephemeral and the paginated message stays as it is.
                    foreach (VirtualCommandReply reply in result.Replies)
                        await component.FollowupAsync(reply.Text, ephemeral: true);
                    return;
                }

                // The captured reply is the clicker's own page: their visible commands,
                // with the buttons rebuilt by the pipeline for that page.
                VirtualCommandReply page = result.Replies[0];
                await component.ModifyOriginalResponseAsync(properties =>
                {
                    properties.Content = page.Text;
                    properties.Components = page.Components;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Help pagination button failed for user {UserId} in channel {ChannelId}.", component.User.Id, component.Channel.Id);

                try
                {
                    if (component.HasResponded)
                        await component.FollowupAsync(SlashCommandService.GenericErrorReply, ephemeral: true);
                    else
                        await component.RespondAsync(SlashCommandService.GenericErrorReply, ephemeral: true);
                }
                catch (Exception replyEx)
                {
                    _logger.LogError(replyEx, "Failed to send the help pagination error reply.");
                }
            }
        }
    }
}
