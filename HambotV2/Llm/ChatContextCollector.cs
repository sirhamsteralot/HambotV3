using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace HambotV2.Llm
{
    /// <summary>
    /// The shared bounded chat reader: the newest messages from the bot's readable guild text
    /// channels, merged newest-first and capped — never DMs. Behind an interface so the loops
    /// and prompt assembly are unit-testable without a gateway connection.
    /// </summary>
    internal interface IChatContextCollector
    {
        Task<string?> CollectAsync(int maxMessages, int maxCharacters, CancellationToken cancellationToken = default);
    }

    internal sealed class ChatContextCollector : IChatContextCollector
    {
        private readonly DiscordSocketClient _client;
        private readonly ILogger<ChatContextCollector> _logger;

        public ChatContextCollector(DiscordSocketClient client, ILogger<ChatContextCollector> logger)
        {
            _client = client;
            _logger = logger;
        }

        public async Task<string?> CollectAsync(int maxMessages, int maxCharacters, CancellationToken cancellationToken = default)
        {
            var latest = new List<(DateTimeOffset At, string Author, string Text)>();

            foreach (SocketGuild guild in _client.Guilds)
            {
                foreach (SocketTextChannel channel in guild.TextChannels)
                {
                    // A channel the bot cannot read (50001 Missing Access), that was deleted (404),
                    // or that otherwise errors is skipped — the remaining readable channels still
                    // contribute; one bad channel must not fail the whole update.
                    try
                    {
                        await foreach (IReadOnlyCollection<IMessage> page in ((IMessageChannel)channel).GetMessagesAsync(1, CacheMode.AllowDownload, new RequestOptions { CancelToken = cancellationToken }))
                        {
                            foreach (IMessage message in page)
                                latest.Add((message.Timestamp, message.Author.Username, message.Content));
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Skipping channel {ChannelId} in guild {GuildId} for chat context.", channel.Id, guild.Id);
                    }
                }
            }

            latest.Sort(static (a, b) => b.At.CompareTo(a.At));
            return ChatContextFormatter.Format(latest, maxMessages, maxCharacters);
        }
    }

    /// <summary>The pure formatting core: newest-first lines, capped in count and total characters.</summary>
    internal static class ChatContextFormatter
    {
        public static string? Format(IEnumerable<(DateTimeOffset At, string Author, string Text)> messages, int maxMessages, int maxCharacters)
        {
            List<(DateTimeOffset At, string Author, string Text)> taken = messages.Take(maxMessages).ToList();
            if (taken.Count == 0)
                return null;

            var lines = new List<string>(taken.Count);
            foreach (var message in taken)
                lines.Add($"[{message.At:yyyy-MM-dd HH:mm} UTC] {message.Author}: {message.Text.ReplaceLineEndings(" ")}");

            // The character cap trims whole lines from the oldest end, never mid-line.
            int total = lines.Sum(line => line.Length);
            while (lines.Count > 1 && total > maxCharacters)
                total -= lines[^1].Length + 1;
            if (lines.Count > 1 && total > maxCharacters)
                lines.RemoveAt(lines.Count - 1);

            return string.Join('\n', lines);
        }
    }
}
