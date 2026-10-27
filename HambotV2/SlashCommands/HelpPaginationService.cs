using Discord;
using Discord.WebSocket;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;

namespace HambotV2.SlashCommands
{
    /// <summary>
    /// Gateway glue for the interactive help pagination: subscribes the shared client's
    /// button events and hands each help-button click to <see cref="HelpPagination.HandleClickAsync"/>
    /// (invoker-owned paging through the virtual-invocation seam). Attached unconditionally:
    /// the buttons ride on text-surface help messages too, so paging works regardless of
    /// <c>SlashCommands:Enabled</c>.
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
            try
            {
                await HelpPagination.HandleClickAsync(new SocketPaginationButton(component), _virtualCommandRunner);
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

        /// <summary>Adapts one gateway button click to the gateway-free pagination core.</summary>
        private sealed class SocketPaginationButton : IHelpPaginationButton
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

            public Task ModifyListInPlaceAsync(string content, MessageComponent components)
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
