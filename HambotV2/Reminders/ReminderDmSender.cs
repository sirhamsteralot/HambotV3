using Discord;
using Discord.WebSocket;
using HambotV2.Scheduling;

namespace HambotV2.Reminders
{
    /// <summary>
    /// Delivers reminder DMs through the shared gateway client. The gateway's user cache may not
    /// know the owner (e.g. right after a restart, before they have spoken again), so the REST
    /// client serves as the fallback; a user resolvable through neither is a delivery failure
    /// handled by the engine's bounded retry.
    /// </summary>
    internal sealed class ReminderDmSender : IScheduledMessageSender<ScheduledReminderMessage>
    {
        private readonly DiscordSocketClient _client;

        public ReminderDmSender(DiscordSocketClient client)
        {
            _client = client;
        }

        public async Task SendAsync(ScheduledReminderMessage message, CancellationToken cancellationToken)
        {
            IUser user = (IUser?)_client.GetUser(message.OwnerDiscordId)
                ?? await _client.Rest.GetUserAsync(message.OwnerDiscordId);

            await user.SendMessageAsync(message.Message, options: new RequestOptions { CancelToken = cancellationToken });
        }
    }
}
