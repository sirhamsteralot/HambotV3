using Discord;
using Discord.WebSocket;

namespace HambotV2.Connections
{
    /// <summary>
    /// <see cref="IPresenceGateway"/> over the shared gateway client. The roadmap spike confirmed
    /// Discord.Net 3.20.2 supports the profile routes natively: the per-guild member route is
    /// <c>IGuild.ModifyCurrentUserAsync</c> (<c>SelfGuildUserProperties.Bio/Avatar/Banner</c>, all
    /// clearable as specified-null), and the global avatar route is <c>ISelfUser.ModifyAsync</c>.
    /// There is no global banner route for bots, so banners apply per guild only.
    /// </summary>
    internal sealed class DiscordPresenceGateway : IPresenceGateway
    {
        private readonly DiscordSocketClient _client;
        private volatile bool _ready;

        public DiscordPresenceGateway(DiscordSocketClient client)
        {
            _client = client;

            // Subscribe here, not lazily: the service is resolved before the gateway hosted
            // service starts, so the Ready event cannot be missed.
            _client.Ready += OnReadyAsync;
        }

        public event Func<Task>? Ready;

        public bool IsReady => _ready;

        public Task SetActivityAsync(ActivityType type, string? text)
        {
            // SetGameAsync with a null name clears the activity; streaming needs a stream URL.
            return _client.SetGameAsync(text, null, type);
        }

        public IReadOnlyCollection<ulong> GetGuildIds()
        {
            return _client.Guilds.Select(guild => guild.Id).ToArray();
        }

        public Task SetGuildBioAsync(ulong guildId, string? bio)
        {
            return ModifyCurrentMemberAsync(guildId, properties => properties.Bio = bio);
        }

        public Task SetGuildAvatarAsync(ulong guildId, byte[]? imageData)
        {
            return ModifyCurrentMemberAsync(guildId, properties => properties.Avatar = ToOptionalImage(imageData));
        }

        public Task SetGuildBannerAsync(ulong guildId, byte[]? imageData)
        {
            return ModifyCurrentMemberAsync(guildId, properties => properties.Banner = ToOptionalImage(imageData));
        }

        public async Task SetGlobalAvatarAsync(byte[]? imageData)
        {
            await ((ISelfUser)_client.CurrentUser).ModifyAsync(properties => properties.Avatar = ToOptionalImage(imageData));
        }

        public string? GetAvatarUrl()
        {
            return _client.CurrentUser?.GetAvatarUrl();
        }

        public string? GetBannerUrl()
        {
            // Bots have no global banner, so the first member guild reporting one is the banner.
            foreach (SocketGuild guild in _client.Guilds)
            {
                string? url = guild.CurrentUser?.GetGuildBannerUrl();
                if (!string.IsNullOrEmpty(url))
                    return url;
            }

            return null;
        }

        private async Task OnReadyAsync()
        {
            _ready = true;

            if (Ready is null)
                return;

            foreach (Func<Task> handler in Ready.GetInvocationList().Cast<Func<Task>>())
                await handler();
        }

        private async Task ModifyCurrentMemberAsync(ulong guildId, Action<SelfGuildUserProperties> properties)
        {
            SocketGuild? guild = _client.GetGuild(guildId);
            if (guild is null)
                throw new InvalidOperationException($"Guild {guildId} is not in the gateway cache; its member profile cannot be modified.");

            await guild.ModifyCurrentUserAsync(properties);
        }

        /// <summary>A fresh Image per request (each upload consumes its stream); specified-null clears the field.</summary>
        private static Optional<Image?> ToOptionalImage(byte[]? imageData)
        {
            return imageData is null
                ? new Optional<Image?>(null)
                : new Optional<Image?>((Image?)new Image(new MemoryStream(imageData)));
        }
    }
}
