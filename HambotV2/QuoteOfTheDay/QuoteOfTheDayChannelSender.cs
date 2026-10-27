using Discord;
using Discord.WebSocket;

namespace HambotV2.QuoteOfTheDay
{
    /// <summary>
    /// The transport seam behind the quote-of-the-day publication loop: posts one rendered
    /// message into a channel by id. An unavailable channel or a missing Send Messages
    /// permission surfaces as an exception the loop handles per boundary — there is no upfront
    /// validation at configuration time, and a failing channel never disables the feature.
    /// </summary>
    internal interface IQuoteOfTheDayChannelSender
    {
        Task SendAsync(ulong channelId, string message, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The bot's implementation: resolves the channel on the shared gateway client. The
    /// publication reads no channel messages, so the feature needs no additional gateway intents.
    /// </summary>
    internal sealed class DiscordQuoteOfTheDayChannelSender : IQuoteOfTheDayChannelSender
    {
        private readonly DiscordSocketClient _client;

        public DiscordQuoteOfTheDayChannelSender(DiscordSocketClient client)
        {
            _client = client;
        }

        public async Task SendAsync(ulong channelId, string message, CancellationToken cancellationToken)
        {
            if (_client.GetChannel(channelId) is not IMessageChannel channel)
                throw new InvalidOperationException($"Channel {channelId} is not available to the bot.");

            await channel.SendMessageAsync(message);
        }
    }
}
