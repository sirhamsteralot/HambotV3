using Discord;
using Discord.Rest;

namespace HambotV2.Commands
{
    /// <summary>
    /// The capturing reply channel of a virtual command invocation (design §3.15/§3.11): it
    /// wraps the origin channel the interaction arrived on (so the pipeline's guild scope and
    /// audit data stay real) and records every reply instead of posting it — the interaction
    /// handler re-posts the captured replies as the interaction's response, publicly for normal
    /// command output, ephemerally for denials.
    /// </summary>
    internal class VirtualCommandChannel : IMessageChannel
    {
        private readonly IMessageChannel _origin;

        public VirtualCommandChannel(IMessageChannel origin)
        {
            _origin = origin;
        }

        /// <summary>The replies the pipeline sent, in order.</summary>
        public List<string> SentMessages { get; } = [];

        public ulong Id => _origin.Id;
        public string Name => _origin.Name;
        public ChannelType ChannelType => _origin.ChannelType;
        public DateTimeOffset CreatedAt => _origin.CreatedAt;

        public Task<IUserMessage> SendMessageAsync(string text = "", bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null)
        {
            SentMessages.Add(text);
            return Task.FromResult<IUserMessage>(null!);
        }

        public Task<IUserMessage> SendFileAsync(FileAttachment attachment, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null)
            => throw Unsupported(nameof(SendFileAsync));

        public Task<IUserMessage> SendFileAsync(string filePath, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, bool isSpoiler = false, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null)
            => throw Unsupported(nameof(SendFileAsync));

        public Task<IUserMessage> SendFileAsync(Stream stream, string filename, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, bool isSpoiler = false, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null)
            => throw Unsupported(nameof(SendFileAsync));

        public Task<IUserMessage> SendFilesAsync(IEnumerable<FileAttachment> attachments, string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? messageReference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None, PollProperties? poll = null)
            => throw Unsupported(nameof(SendFilesAsync));

        public Task<IMessage> GetMessageAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null)
            => throw Unsupported(nameof(GetMessageAsync));

        public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(int limit = DiscordConfig.MaxMessagesPerBatch, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null)
            => throw Unsupported(nameof(GetMessagesAsync));

        public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(ulong fromMessageId, Direction dir, int limit = DiscordConfig.MaxMessagesPerBatch, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null)
            => throw Unsupported(nameof(GetMessagesAsync));

        public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(IMessage fromMessage, Direction dir, int limit = DiscordConfig.MaxMessagesPerBatch, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null)
            => throw Unsupported(nameof(GetMessagesAsync));

        public Task<IReadOnlyCollection<IMessage>> GetPinnedMessagesAsync(RequestOptions? options = null)
            => throw Unsupported(nameof(GetPinnedMessagesAsync));

        public Task TriggerTypingAsync(RequestOptions? options = null)
            => Task.CompletedTask;

        public IDisposable EnterTypingState(RequestOptions? options = null)
            => throw Unsupported(nameof(EnterTypingState));

        public Task DeleteMessageAsync(ulong messageId, RequestOptions? options = null)
            => throw Unsupported(nameof(DeleteMessageAsync));

        public Task DeleteMessageAsync(IMessage message, RequestOptions? options = null)
            => throw Unsupported(nameof(DeleteMessageAsync));

        public Task<IUserMessage> ModifyMessageAsync(ulong messageId, Action<MessageProperties> func, RequestOptions? options = null)
            => throw Unsupported(nameof(ModifyMessageAsync));

        public Task<IUser> GetUserAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null)
            => throw Unsupported(nameof(GetUserAsync));

        public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetUsersAsync(CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null)
            => throw Unsupported(nameof(GetUsersAsync));

        private protected static NotSupportedException Unsupported(string operation)
        {
            return new NotSupportedException($"'{operation}' is not part of the virtual command invocation seam (the reply is captured, not posted).");
        }
    }

    /// <summary>
    /// The guild variant of <see cref="VirtualCommandChannel"/>: the origin channel's guild data
    /// (id and owning guild) flows through, so the pipeline resolves the guild scope, the
    /// guild-owner tier override, and the guild's command identifier exactly as for a real
    /// guild message.
    /// </summary>
    internal sealed class VirtualGuildCommandChannel : VirtualCommandChannel, IGuildChannel
    {
        private readonly IGuildChannel _originGuildChannel;

        public VirtualGuildCommandChannel(IMessageChannel origin)
            : base(origin)
        {
            // The origin of a guild interaction is the guild's text channel (an IGuildChannel);
            // a non-guild origin would silently mis-scope the invocation, so fail loudly instead.
            _originGuildChannel = origin as IGuildChannel
                ?? throw new ArgumentException("The guild virtual channel requires a guild channel origin.", nameof(origin));
        }

        public ulong GuildId => _originGuildChannel.GuildId;
        public IGuild Guild => _originGuildChannel.Guild;
        public int Position => _originGuildChannel.Position;
        public ChannelFlags Flags => _originGuildChannel.Flags;
        public IReadOnlyCollection<Overwrite> PermissionOverwrites => _originGuildChannel.PermissionOverwrites;

        public OverwritePermissions? GetPermissionOverwrite(IUser user) => _originGuildChannel.GetPermissionOverwrite(user);
        public OverwritePermissions? GetPermissionOverwrite(IRole role) => _originGuildChannel.GetPermissionOverwrite(role);
        public Task AddPermissionOverwriteAsync(IUser user, OverwritePermissions permissions, RequestOptions? options = null) => throw Unsupported(nameof(AddPermissionOverwriteAsync));
        public Task AddPermissionOverwriteAsync(IRole role, OverwritePermissions permissions, RequestOptions? options = null) => throw Unsupported(nameof(AddPermissionOverwriteAsync));
        public Task RemovePermissionOverwriteAsync(IUser user, RequestOptions? options = null) => throw Unsupported(nameof(RemovePermissionOverwriteAsync));
        public Task RemovePermissionOverwriteAsync(IRole role, RequestOptions? options = null) => throw Unsupported(nameof(RemovePermissionOverwriteAsync));
        public Task ModifyAsync(Action<GuildChannelProperties> func, RequestOptions? options = null) => throw Unsupported(nameof(ModifyAsync));
        public new Task<IGuildUser> GetUserAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) => throw Unsupported(nameof(GetUserAsync));
        public new IAsyncEnumerable<IReadOnlyCollection<IGuildUser>> GetUsersAsync(CacheMode mode = CacheMode.AllowDownload, RequestOptions? options = null) => throw Unsupported(nameof(GetUsersAsync));
        public Task DeleteAsync(RequestOptions? options = null) => throw Unsupported(nameof(DeleteAsync));
    }
}
