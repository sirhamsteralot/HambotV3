using Discord;
using HambotV2.Commands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>/// Shared Moq doubles for the Discord message surface plus an in-memory database, so command
    /// and permission tests run without Discord or PostgreSQL.
    ///</summary>
    internal static class DiscordTestDoubles
    {
        /// <summary>The guild id used by the doubles unless a test needs multiple guilds.</summary>
        public const ulong DefaultGuildId = 1;

        /// <summary>Each caller gets its own isolated in-memory database.</summary>
        public static DatabaseContext CreateDatabase()
        {
            return new DatabaseContext(
                new DbContextOptionsBuilder<DatabaseContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);
        }

        public static Mock<IUser> CreateUser(ulong discordId)
        {
            var user = new Mock<IUser>();
            user.SetupGet(u => u.Id).Returns(discordId);
            return user;
        }

        /// <summary>A fully wired mock command satisfying the ICommand contract (help text included).</summary>
        public static Mock<ICommand> CreateMockCommand(string name, int defaultPermissionTier = 0)
        {
            var command = new Mock<ICommand>();
            command.SetupGet(c => c.Name).Returns(name);
            command.SetupGet(c => c.Description).Returns($"Test command {name}");
            command.SetupGet(c => c.Usage).Returns(name);
            command.SetupGet(c => c.DefaultPermissionTier).Returns(defaultPermissionTier);
            return command;
        }

        public static CommandContext CreateContext(DatabaseContext database, ulong? guildOwnerId = null, ulong? botOwnerId = null, ulong? guildId = DefaultGuildId, IGuildSettingsService? guildSettings = null)
        {
            return new CommandContext(database, guildId, "!", guildSettings ?? Mock.Of<IGuildSettingsService>(), new TierOverrides
            {
                GuildOwnerId = guildOwnerId,
                BotOwnerId = botOwnerId
            });
        }

        /// <summary>A direct-message mock: no guild, so only the configured bot owner can raise a tier.</summary>
        public static Mock<IMessage> CreateDirectMessage(string content, ulong authorId, List<string> sentMessages, DateTimeOffset? timestamp = null)
        {
            var channel = new Mock<IMessageChannel>();
            RecordSends(channel, sentMessages);

            return CreateMessage(channel, content, authorId, timestamp);
        }

        /// <summary>A guild message mock whose channel belongs to a guild owned by <paramref name="guildOwnerId"/>.</summary>
        public static Mock<IMessage> CreateGuildMessage(string content, ulong authorId, ulong guildOwnerId, List<string> sentMessages, ulong guildId = DefaultGuildId, DateTimeOffset? timestamp = null, IAttachment? attachment = null)
        {
            return CreateGuildMessage(content, authorId, guildOwnerId, sentMessages, attachment is null ? [] : [attachment], guildId, timestamp);
        }

        /// <summary>A guild message mock carrying several attachments (e.g. the avatar/banner rejection cases).</summary>
        public static Mock<IMessage> CreateGuildMessage(string content, ulong authorId, ulong guildOwnerId, List<string> sentMessages, IAttachment[] attachments, ulong guildId = DefaultGuildId, DateTimeOffset? timestamp = null)
        {
            var guild = new Mock<IGuild>();
            guild.SetupGet(g => g.OwnerId).Returns(guildOwnerId);

            // ITextChannel is both an IMessageChannel and a guild channel carrying the guild (and
            // with it the owner id used for the guild-owner tier override).
            var channel = new Mock<ITextChannel>();
            channel.SetupGet(c => c.GuildId).Returns(guildId);
            channel.SetupGet(c => c.Guild).Returns(guild.Object);
            RecordSends(channel, sentMessages);

            return CreateMessage(channel, content, authorId, timestamp, attachments);
        }

        /// <summary>An attachment mock with the given CDN url and filename.</summary>
        public static IAttachment CreateAttachment(string url = "https://cdn.example.test/quoteSettings.json", string filename = "quoteSettings.json")
        {
            return Mock.Of<IAttachment>(attachment => attachment.Url == url && attachment.Filename == filename);
        }

        private static Mock<IMessage> CreateMessage<TChannel>(Mock<TChannel> channel, string content, ulong authorId, DateTimeOffset? timestamp = null, IAttachment[]? attachments = null) where TChannel : class, IMessageChannel
        {
            var message = new Mock<IMessage>();
            message.SetupGet(m => m.Content).Returns(content);
            message.SetupGet(m => m.Author).Returns(CreateUser(authorId).Object);
            message.SetupGet(m => m.Channel).Returns(channel.Object);
            message.SetupGet(m => m.Timestamp).Returns(timestamp ?? DateTimeOffset.UnixEpoch);
            message.SetupGet(m => m.Attachments).Returns(attachments ?? []);
            return message;
        }

        private static void RecordSends<TChannel>(Mock<TChannel> channel, List<string> sentMessages) where TChannel : class, IMessageChannel
        {
            channel
                .Setup(c => c.SendMessageAsync(
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<Embed?>(),
                    It.IsAny<RequestOptions?>(),
                    It.IsAny<AllowedMentions?>(),
                    It.IsAny<MessageReference?>(),
                    It.IsAny<MessageComponent>(),
                    It.IsAny<ISticker[]?>(),
                    It.IsAny<Embed[]?>(),
                    It.IsAny<MessageFlags>(),
                    It.IsAny<PollProperties?>()))
                .Callback<string, bool, Embed?, RequestOptions?, AllowedMentions?, MessageReference?, MessageComponent, ISticker[]?, Embed[]?, MessageFlags, PollProperties?>(
                    (text, _, _, _, _, _, _, _, _, _, _) => sentMessages.Add(text))
                .Returns(Task.FromResult<IUserMessage?>(null));
        }
    }
}
