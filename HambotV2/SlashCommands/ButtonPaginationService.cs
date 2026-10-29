using Discord;
using Discord.WebSocket;
using HambotV2.Commands;
using HambotV2.Commands.QuoteCommands;
using HambotV2.Commands.SystemCommands;

namespace HambotV2.SlashCommands
{
    /// <summary>
    /// Gateway glue for the interactive pagination: subscribes the shared client's button events
    /// and hands each click to its feature's pagination core — help pages run back through the
    /// virtual-invocation seam (invoker-owned paging), quote search pages render from the stored
    /// backlog snapshot. Attached unconditionally: the buttons ride on text-surface messages too,
    /// regardless of <c>SlashCommands:Enabled</c>.
    /// </summary>
    internal sealed class ButtonPaginationService
    {
        private readonly DiscordSocketClient _client;
        private readonly IVirtualCommandRunner _virtualCommandRunner;
        private readonly QuoteSearchBacklog _quoteSearchBacklog;
        private readonly ILogger<ButtonPaginationService> _logger;

        public ButtonPaginationService(DiscordSocketClient client, IVirtualCommandRunner virtualCommandRunner, QuoteSearchBacklog quoteSearchBacklog, ILogger<ButtonPaginationService> logger)
        {
            _client = client;
            _virtualCommandRunner = virtualCommandRunner;
            _quoteSearchBacklog = quoteSearchBacklog;
            _logger = logger;
        }

        /// <summary>Subscribes the pagination button handler to the shared gateway client.</summary>
        public void Attach()
        {
            _client.ButtonExecuted += OnButtonExecutedAsync;
        }

        private async Task OnButtonExecutedAsync(SocketMessageComponent component)
        {
            try
            {
                var button = new SocketPaginationButton(component);
                if (component.Data.CustomId.StartsWith(QuoteSearchPagination.CustomIdPrefix, StringComparison.Ordinal))
                    await QuoteSearchPagination.HandleClickAsync(button, _quoteSearchBacklog);
                else
                    await HelpPagination.HandleClickAsync(button, _virtualCommandRunner);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pagination button failed for user {UserId} in channel {ChannelId}.", component.User.Id, component.Channel.Id);

                try
                {
                    if (component.HasResponded)
                        await component.FollowupAsync(SlashCommandService.GenericErrorReply, ephemeral: true);
                    else
                        await component.RespondAsync(SlashCommandService.GenericErrorReply, ephemeral: true);
                }
                catch (Exception replyEx)
                {
                    _logger.LogError(replyEx, "Failed to send the pagination error reply.");
                }
            }
        }

        /// <summary>Adapts one gateway button click to the gateway-free pagination core.</summary>
        private sealed class SocketPaginationButton : IPaginationButton
        {
            private readonly SocketMessageComponent _component;

            public SocketPaginationButton(SocketMessageComponent component)
            {
                _component = component;
            }

            public IUser User => _component.User;
            public string CustomId => _component.Data.CustomId;
            public IMessageChannel Channel => _component.Channel;
            public DateTimeOffset CreatedAt => _component.CreatedAt;

            public Task DeferAsync()
            {
                return _component.DeferAsync();
            }

            public Task SendEphemeralAsync(string text)
            {
                return _component.FollowupAsync(text, ephemeral: true);
            }

            public Task ModifyListInPlaceAsync(string content, MessageComponent? components)
            {
                return _component.ModifyOriginalResponseAsync(properties =>
                {
                    properties.Content = content;
                    properties.Components = components;
                });
            }
        }
    }
}
