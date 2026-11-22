using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using HambotV2.Commands;
using HambotV2.Connections;
using HambotV2.Maintenance;

namespace HambotV2.LinkFix
{
    /// <summary>
    /// The link-repost listener: in a guild that opted in, a member message carrying an
    /// X/Twitter or Reddit link gets one follow-up with the same posts on a fixed-embed mirror.
    /// Purely textual — nothing is fetched, no LLM or gate budget is touched, and nothing about a
    /// link or a message is stored or logged. The original message is never edited or deleted (a
    /// bot cannot edit a member's message). A singleton with its own <see cref="Attach"/>, not a
    /// hosted service: it only subscribes, so there is no loop to host.
    /// </summary>
    internal sealed class EmbedLinkRepostService
    {
        private readonly ILinkMappingStore _mappings;
        private readonly LinkFixSettings _settings;
        private readonly IGuildSettingsService _guildSettings;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<EmbedLinkRepostService> _logger;
        private readonly IMaintenanceMode? _maintenanceMode;

        /// <summary>Each channel's last repost, for <c>LinkFix:ChannelCooldownSeconds</c>; a restart resets it.</summary>
        private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastRepostAtByChannel = new();

        public EmbedLinkRepostService(
            ILinkMappingStore mappings,
            LinkFixSettings settings,
            IGuildSettingsService guildSettings,
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            ILogger<EmbedLinkRepostService> logger,
            IMaintenanceMode? maintenanceMode = null)
        {
            _mappings = mappings;
            _settings = settings;
            _guildSettings = guildSettings;
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _logger = logger;
            _maintenanceMode = maintenanceMode;
        }

        /// <summary>Subscribes the repost handler to the shared gateway client, beside the pagination attach.</summary>
        public void Attach(DiscordSocketClient client)
        {
            client.MessageReceived += OnMessageReceivedAsync;
        }

        private async Task OnMessageReceivedAsync(SocketMessage message)
        {
            try
            {
                await HandleMessageAsync(message);
            }
            catch (Exception ex)
            {
                // Never content: the message text (and with it a link) stays out of the log.
                _logger.LogError(ex, "Link repost handling failed for message {MessageId} in channel {ChannelId}.", message.Id, message.Channel.Id);
            }
        }

        /// <summary>One message through the skip rules, the rewrite, and the bounds; anything that fails is dropped.</summary>
        internal async Task HandleMessageAsync(IMessage message)
        {
            // Nothing is scanned during a restore, and nothing is queued for later.
            if (_maintenanceMode?.IsActive == true)
                return;

            // DMs are never scanned or answered.
            if (message.Channel is not IGuildChannel guildChannel)
                return;

            // Bot and webhook authors, the bot itself included — which also makes a repost loop impossible.
            if (message.Author.IsBot || message.Author.IsWebhook)
                return;

            // Command invocations belong to the pipeline.
            if (message.Content.StartsWith(_guildSettings.GetCommandIdentifier(guildChannel.GuildId), StringComparison.Ordinal))
                return;

            // The textual gate: no database is touched for a message without a matching host.
            LinkMapping mapping = await _mappings.GetAsync();
            if (!LinkRewriter.HasMappableLink(message.Content, mapping))
                return;

            if (!await IsOptedInAsync(guildChannel.GuildId))
                return;

            IReadOnlyList<string> links = LinkRewriter.Rewrite(message.Content, mapping, _settings.MaxLinksPerMessage);
            if (links.Count == 0)
                return;

            if (!TryClaimChannelRepost(message.Channel.Id, _timeProvider.GetUtcNow(), out DateTimeOffset claim))
                return;

            string content = string.Join('\n', links);
            try
            {
                await message.Channel.SendMessageAsync(
                    content,
                    allowedMentions: AllowedMentions.None,
                    messageReference: new MessageReference(message.Id, message.Channel.Id, failIfNotExists: false));

                _logger.LogInformation(
                    "Link repost {Outcome} in guild {GuildId}, channel {ChannelId}: {LinkCount} link(s).",
                    "succeeded", guildChannel.GuildId, message.Channel.Id, links.Count);
            }
            catch (Exception ex)
            {
                // Logged without content, swallowed, never retried: a failed repost is not a health
                // signal. The claimed window is handed back — only a posted repost consumes it.
                ReleaseChannelRepost(message.Channel.Id, claim);
                _logger.LogError(ex,
                    "Link repost {Outcome} in guild {GuildId}, channel {ChannelId}: {LinkCount} link(s).",
                    "failed", guildChannel.GuildId, message.Channel.Id, links.Count);
            }
        }

        /// <summary>The guild's opt-in: the one database read, made only for a message that matched.</summary>
        private async Task<bool> IsOptedInAsync(ulong guildId, CancellationToken cancellationToken = default)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
            return await database.GuildSettings.FindAsync([guildId], cancellationToken) is { LinkFixEnabled: true };
        }

        /// <summary>
        /// Reserves the channel's cooldown window; false while the window is still running. Zero or
        /// negative <c>LinkFix:ChannelCooldownSeconds</c> disables the bound entirely. The claim is
        /// taken before the send so two concurrent messages cannot both post, and handed back by
        /// <see cref="ReleaseChannelRepost"/> when the send fails.
        /// </summary>
        private bool TryClaimChannelRepost(ulong channelId, DateTimeOffset now, out DateTimeOffset claim)
        {
            claim = now;

            TimeSpan cooldown = _settings.ChannelCooldown;
            if (cooldown <= TimeSpan.Zero)
                return true;

            while (true)
            {
                if (!_lastRepostAtByChannel.TryGetValue(channelId, out DateTimeOffset lastRepostAt))
                {
                    if (_lastRepostAtByChannel.TryAdd(channelId, now))
                        return true;

                    continue;
                }

                if (now - lastRepostAt < cooldown)
                    return false;

                if (_lastRepostAtByChannel.TryUpdate(channelId, now, lastRepostAt))
                    return true;
            }
        }

        /// <summary>Hands back a window claimed for a repost that never posted, unless another message has claimed since.</summary>
        private void ReleaseChannelRepost(ulong channelId, DateTimeOffset claim)
        {
            ((ICollection<KeyValuePair<ulong, DateTimeOffset>>)_lastRepostAtByChannel)
                .Remove(new KeyValuePair<ulong, DateTimeOffset>(channelId, claim));
        }
    }
}
