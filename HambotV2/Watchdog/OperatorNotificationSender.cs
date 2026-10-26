using Discord;
using Discord.WebSocket;

namespace HambotV2.Watchdog
{
    /// <summary>
    /// The transport for operator notifications: one component posting to the configured
    /// channel and DMing the bot owner over the shared gateway client, behind an interface so the
    /// publisher and watchdog are unit-testable with a mocked sender.
    /// </summary>
    internal interface IOperatorNotificationSender
    {
        /// <summary>Posts the message to the given channel; throws when the channel is not reachable.</summary>
        Task SendToChannelAsync(ulong channelId, string message);

        /// <summary>Direct-messages the given user; throws when the user is not reachable (e.g. closed DMs).</summary>
        Task SendOwnerDmAsync(ulong userId, string message);
    }

    internal sealed class DiscordNotificationSender : IOperatorNotificationSender
    {
        private readonly DiscordSocketClient _client;

        public DiscordNotificationSender(DiscordSocketClient client)
        {
            _client = client;
        }

        public async Task SendToChannelAsync(ulong channelId, string message)
        {
            // The gateway cache first, the REST client as fallback — the same resolution order the
            // reminder DMs use: the cache does not know a channel right after a restart.
            IMessageChannel? channel = _client.GetChannel(channelId) as IMessageChannel;
            if (channel is null)
                channel = await _client.Rest.GetChannelAsync(channelId) as IMessageChannel;

            if (channel is null)
                throw new InvalidOperationException($"Channel {channelId} is not visible to the bot.");

            await channel.SendMessageAsync(message);
        }

        public async Task SendOwnerDmAsync(ulong userId, string message)
        {
            IUser user = (IUser?)_client.GetUser(userId) ?? await _client.Rest.GetUserAsync(userId);
            if (user is null)
                throw new InvalidOperationException($"User {userId} is not resolvable.");

            await user.SendMessageAsync(message);
        }
    }
}
