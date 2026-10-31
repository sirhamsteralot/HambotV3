using Discord.WebSocket;

namespace HambotV2.Connections
{
    /// <summary>
    /// Read-only lookup of a guild's channels over the shared gateway client, so commands can
    /// validate channel arguments without touching Discord.Net directly (and tests can fake it).
    /// </summary>
    internal interface IGuildChannelLookup
    {
        /// <summary>True when the channel is a text channel of the given guild.</summary>
        bool IsTextChannelOf(ulong guildId, ulong channelId);
    }

    internal sealed class DiscordGuildChannelLookup : IGuildChannelLookup
    {
        private readonly DiscordSocketClient _client;

        public DiscordGuildChannelLookup(DiscordSocketClient client)
        {
            _client = client;
        }

        public bool IsTextChannelOf(ulong guildId, ulong channelId) =>
            _client.GetGuild(guildId)?.GetTextChannel(channelId) is not null;
    }
}
