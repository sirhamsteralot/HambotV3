using Discord;

namespace HambotV2.Commands
{
    /// <summary>
    /// The virtual <see cref="IMessage"/> of a non-gateway invocation (design §3.15/§3.11): it
    /// carries the invoking user, origin channel, timestamp, and the equivalent text command
    /// line, so the real command pipeline runs unchanged — tiers, cooldowns, audit, and errors
    /// included. Everything the pipeline and commands do not use is absent: no attachments, no
    /// reactions, no embeds.
    /// </summary>
    internal sealed class VirtualCommandMessage : IMessage
    {
        public VirtualCommandMessage(IUser author, IMessageChannel channel, string content, DateTimeOffset timestamp)
        {
            Id = 0;
            Author = author;
            Channel = channel;
            Content = content;
            Timestamp = timestamp;
        }

        public ulong Id { get; }
        public IUser Author { get; }
        public IMessageChannel Channel { get; }
        /// <summary>The equivalent text-surface command line, identifier included (e.g. <c>!userpermission &lt;@1&gt; 2</c>).</summary>
        public string Content { get; }
        public DateTimeOffset Timestamp { get; }
        public DateTimeOffset CreatedAt => Timestamp;

        public MessageActivity? Activity => null;
        public PurchaseNotification PurchaseNotification => default;
        public MessageApplication? Application => null;
        public IReadOnlyCollection<IAttachment> Attachments => [];
        public MessageCallData? CallData => null;
        public string CleanContent => Content;
        public IReadOnlyCollection<IMessageComponent> Components => [];
        public DateTimeOffset? EditedTimestamp => null;
        public IReadOnlyCollection<IEmbed> Embeds => [];
        public MessageFlags? Flags => null;
        public IMessageInteraction? Interaction => null;
        public bool IsPinned => false;
        public bool IsSuppressed => false;
        public bool IsTTS => false;
        public IReadOnlyCollection<ulong> MentionedChannelIds => [];
        public bool MentionedEveryone => false;
        public IReadOnlyCollection<ulong> MentionedRoleIds => [];
        public IReadOnlyCollection<ulong> MentionedUserIds => [];
        public MessageReference? Reference => null;
        public MessageRoleSubscriptionData RoleSubscriptionData => null!;
        public IReadOnlyDictionary<IEmote, ReactionMetadata> Reactions => new Dictionary<IEmote, ReactionMetadata>();
        public IReadOnlyCollection<ITag> Tags => [];
        public IReadOnlyCollection<IStickerItem> Stickers => [];
        public IThreadChannel? Thread => null;
        public MessageType Type => MessageType.Default;
        public MessageSource Source => MessageSource.User;

        public Task AddReactionAsync(IEmote emote, RequestOptions? options = null) => throw Unsupported(nameof(AddReactionAsync));
        public Task RemoveReactionAsync(IEmote emote, ulong userId, RequestOptions? options = null) => throw Unsupported(nameof(RemoveReactionAsync));
        public Task RemoveReactionAsync(IEmote emote, IUser user, RequestOptions? options = null) => throw Unsupported(nameof(RemoveReactionAsync));
        public Task RemoveAllReactionsAsync(RequestOptions? options = null) => throw Unsupported(nameof(RemoveAllReactionsAsync));
        public Task RemoveAllReactionsForEmoteAsync(IEmote emote, RequestOptions? options = null) => throw Unsupported(nameof(RemoveAllReactionsForEmoteAsync));
        public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetReactionUsersAsync(IEmote emoji, int limit, RequestOptions? options = null, ReactionType type = ReactionType.Normal) => throw Unsupported(nameof(GetReactionUsersAsync));
        public Task DeleteAsync(RequestOptions? options = null) => throw Unsupported(nameof(DeleteAsync));

        private static NotSupportedException Unsupported(string operation)
        {
            return new NotSupportedException($"'{operation}' is not part of the virtual command invocation seam.");
        }
    }
}
