using Discord;
using HambotV2.Commands;
using HambotV2.Commands.LinkFixCommands;
using HambotV2.Connections;
using HambotV2.LinkFix;
using HambotV2.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestLinkFix
    {
        private const ulong GuildId = 41;
        private const ulong OtherGuildId = 42;
        private const ulong ChannelId = 800;
        private const ulong AuthorId = 555;
        private const ulong MessageId = 900;

        private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        /// <summary>One captured send: the text plus the two options the repost contract fixes.</summary>
        private sealed record CapturedSend(string Text, AllowedMentions? AllowedMentions, MessageReference? Reference);

        /// <summary>Captures log lines so a test can prove no link or message text ever reaches the log.</summary>
        private sealed class RecordingLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Messages.Add(formatter(state, exception));
            }
        }

        private sealed class Harness
        {
            public Harness(LinkFixSettings? settings = null)
            {
                Settings = settings ?? new LinkFixSettings();

                _guildSettings = new Mock<IGuildSettingsService>();
                _guildSettings.SetupGet(service => service.GlobalCommandIdentifier).Returns("!");
                _guildSettings.Setup(service => service.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");
                _scopeFactory = ScopeFactoryFor(Database);

                Store = new LinkMappingStore(_scopeFactory, Settings, NullLogger<LinkMappingStore>.Instance);
                Service = NewService();
            }

            private readonly Mock<IGuildSettingsService> _guildSettings;
            private readonly IServiceScopeFactory _scopeFactory;

            /// <summary>A fresh service instance: a restart has no in-memory cooldown state.</summary>
            public EmbedLinkRepostService NewService() =>
                new(Store, Settings, _guildSettings.Object, _scopeFactory, Time, Logger, Maintenance);

            public DatabaseContext Database { get; } = DiscordTestDoubles.CreateDatabase();
            public FakeTimeProvider Time { get; } = new(StartTime);
            public MaintenanceMode Maintenance { get; } = new();
            public RecordingLogger<EmbedLinkRepostService> Logger { get; } = new();
            public LinkFixSettings Settings { get; }
            public List<CapturedSend> Sends { get; } = new();
            public LinkMappingStore Store { get; }
            public EmbedLinkRepostService Service { get; }
        }

        private static IServiceScopeFactory ScopeFactoryFor(DatabaseContext database)
        {
            var services = new ServiceCollection();
            // Singleton so a scope created per message never disposes the shared test database.
            services.AddSingleton<IDatabaseContext>(database);
            return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        private static void OptIn(DatabaseContext database, ulong guildId = GuildId, bool enabled = true)
        {
            database.GuildSettings.Add(new DatabaseContext.GuildSetting { GuildId = guildId, LinkFixEnabled = enabled });
            database.SaveChanges();
        }

        private static Mock<ITextChannel> CreateChannel(List<CapturedSend> sends, bool failSends = false, ulong channelId = ChannelId, ulong guildId = GuildId)
        {
            var guild = new Mock<IGuild>();
            guild.SetupGet(g => g.OwnerId).Returns(999);

            var channel = new Mock<ITextChannel>();
            channel.SetupGet(c => c.Id).Returns(channelId);
            channel.SetupGet(c => c.GuildId).Returns(guildId);
            channel.SetupGet(c => c.Guild).Returns(guild.Object);

            var send = channel.Setup(c => c.SendMessageAsync(
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
                It.IsAny<PollProperties?>()));

            if (failSends)
                send.ThrowsAsync(new InvalidOperationException("simulated send failure"));
            else
                send.Callback<string, bool, Embed?, RequestOptions?, AllowedMentions?, MessageReference?, MessageComponent, ISticker[]?, Embed[]?, MessageFlags, PollProperties?>(
                    (text, _, _, _, allowedMentions, reference, _, _, _, _, _) => sends.Add(new CapturedSend(text, allowedMentions, reference)))
                    .Returns(Task.FromResult<IUserMessage?>(null));

            return channel;
        }

        private static IMessage CreateMessage(ITextChannel channel, string content, ulong authorId = AuthorId, bool isBot = false, bool isWebhook = false, ulong messageId = MessageId)
        {
            var author = new Mock<IUser>();
            author.SetupGet(user => user.Id).Returns(authorId);
            author.SetupGet(user => user.IsBot).Returns(isBot);
            author.SetupGet(user => user.IsWebhook).Returns(isWebhook);

            var message = new Mock<IMessage>();
            message.SetupGet(m => m.Id).Returns(messageId);
            message.SetupGet(m => m.Content).Returns(content);
            message.SetupGet(m => m.Author).Returns(author.Object);
            message.SetupGet(m => m.Channel).Returns(channel);
            return message.Object;
        }

        /// <summary>The payload is exactly AllowedMentions.None: nobody is pinged, not even the replied-to author.</summary>
        private static void AssertNoMentions(AllowedMentions? allowedMentions)
        {
            Assert.AreSame(AllowedMentions.None, allowedMentions, "The repost must send AllowedMentions.None.");
        }

        private static IConfiguration Config(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        // --- settings ---

        [TestMethod]
        public void Settings_Absent_Section_Uses_The_Default_Bounds()
        {
            LinkFixSettings settings = LinkFixSettings.FromConfiguration(Config(new Dictionary<string, string?>()));

            Assert.AreEqual(LinkFixSettings.DefaultMaxLinksPerMessage, settings.MaxLinksPerMessage);
            Assert.AreEqual(TimeSpan.Zero, settings.ChannelCooldown, "The cooldown ships disabled.");
            Assert.AreEqual(LinkFixSettings.DefaultMaxMappings, settings.MaxMappings);
        }

        [TestMethod]
        public void Settings_Configured_Values_Apply_And_Invalid_Ones_Fall_Back()
        {
            LinkFixSettings configured = LinkFixSettings.FromConfiguration(Config(new Dictionary<string, string?>
            {
                ["LinkFix:MaxLinksPerMessage"] = "2",
                ["LinkFix:ChannelCooldownSeconds"] = "30",
                ["LinkFix:MaxMappings"] = "10"
            }));

            Assert.AreEqual(2, configured.MaxLinksPerMessage);
            Assert.AreEqual(TimeSpan.FromSeconds(30), configured.ChannelCooldown);
            Assert.AreEqual(10, configured.MaxMappings);

            LinkFixSettings invalid = LinkFixSettings.FromConfiguration(Config(new Dictionary<string, string?>
            {
                ["LinkFix:MaxLinksPerMessage"] = "0",
                ["LinkFix:ChannelCooldownSeconds"] = "-5",
                ["LinkFix:MaxMappings"] = "not-a-number"
            }));

            Assert.AreEqual(LinkFixSettings.DefaultMaxLinksPerMessage, invalid.MaxLinksPerMessage);
            Assert.AreEqual(TimeSpan.Zero, invalid.ChannelCooldown, "A negative cooldown leaves the bound disabled.");
            Assert.AreEqual(LinkFixSettings.DefaultMaxMappings, invalid.MaxMappings);
        }

        // --- hostname validation ---

        [TestMethod]
        public void Hostnames_Normalize_Case_And_Whitespace_But_Reject_Anything_But_Bare_Hosts()
        {
            Assert.IsTrue(LinkHostNames.TryNormalize("  X.COM ", out string? normalized));
            Assert.AreEqual("x.com", normalized);

            foreach (string rejected in new[]
            {
                "https://x.com", "x.com/path", "x.com:443", "user@x.com", "*.x.com", "localhost", "x..com",
                "-x.com", "x-.com", "x com", "", "   ", new string('a', 250) + ".com"
            })
            {
                Assert.IsFalse(LinkHostNames.TryNormalize(rejected, out _), $"'{rejected}' is not a bare hostname.");
            }
        }

        // --- rewrite ---

        [TestMethod]
        public void Rewrite_Swaps_The_Host_For_Every_Origin_And_Subdomain_Keeping_Path_Query_And_Fragment()
        {
            LinkMapping mapping = LinkMapping.Shipped;

            CollectionAssert.AreEqual(
                new[] { "https://fxtwitter.com/hamster/status/123" },
                LinkRewriter.Rewrite("https://x.com/hamster/status/123", mapping, 5).ToList());

            foreach (string host in new[] { "www.x.com", "mobile.x.com", "m.x.com", "X.COM", "www.twitter.com", "mobile.twitter.com", "twitter.com" })
            {
                CollectionAssert.AreEqual(
                    new[] { "https://fxtwitter.com/hamster/status/123" },
                    LinkRewriter.Rewrite($"https://{host}/hamster/status/123", mapping, 5).ToList(),
                    $"'{host}' must be rewritten to the X mirror.");
            }

            foreach (string host in new[] { "reddit.com", "www.reddit.com", "old.reddit.com", "new.reddit.com", "np.reddit.com", "m.reddit.com" })
            {
                CollectionAssert.AreEqual(
                    new[] { "https://redditez.com/r/dotnet/comments/abc123/a_title/?context=3#top" },
                    LinkRewriter.Rewrite($"https://{host}/r/dotnet/comments/abc123/a_title/?context=3#top", mapping, 5).ToList(),
                    $"'{host}' must be rewritten to the Reddit mirror.");
            }
        }

        [TestMethod]
        public void Rewrite_Forces_Https_And_Ignores_Hosts_The_Mapping_Does_Not_Know()
        {
            LinkMapping mapping = LinkMapping.Shipped;

            CollectionAssert.AreEqual(
                new[] { "https://fxtwitter.com/hamster/status/9" },
                LinkRewriter.Rewrite("http://twitter.com/hamster/status/9", mapping, 5).ToList(),
                "A plain-http link is rewritten to https.");

            Assert.AreEqual(0, LinkRewriter.Rewrite("https://example.com/hamster/status/9", mapping, 5).Count, "An unknown host is left alone.");
            Assert.AreEqual(0, LinkRewriter.Rewrite("https://notx.com/hamster/status/9", mapping, 5).Count, "'notx.com' is not a subdomain of 'x.com'.");
            Assert.AreEqual(0, LinkRewriter.Rewrite("no links here at all", mapping, 5).Count);
            Assert.IsFalse(LinkRewriter.HasMappableLink("no links here at all", mapping));
            Assert.IsTrue(LinkRewriter.HasMappableLink("see https://x.com/hamster/status/1", mapping));
        }

        [TestMethod]
        public void Rewrite_Strips_Trailing_Punctuation_And_Writes_Only_The_Urls()
        {
            LinkMapping mapping = LinkMapping.Shipped;
            const string content = "look (https://x.com/hamster/status/1), also [https://x.com/hamster/status/2]. and https://x.com/hamster/status/3>";

            IReadOnlyList<string> rewritten = LinkRewriter.Rewrite(content, mapping, 5);

            CollectionAssert.AreEqual(
                new[]
                {
                    "https://fxtwitter.com/hamster/status/1",
                    "https://fxtwitter.com/hamster/status/2",
                    "https://fxtwitter.com/hamster/status/3"
                },
                rewritten.ToList());
        }

        [TestMethod]
        public void Rewrite_Applies_Each_Path_Shape_And_Skips_Short_Or_Opaque_Shapes()
        {
            LinkMapping mapping = LinkMapping.Shipped;

            foreach (string path in new[] { "/hamster/status/123", "/hamster/status/123/" })
            {
                Assert.AreEqual(1, LinkRewriter.Rewrite($"https://x.com{path}", mapping, 5).Count, $"'{path}' is an X post path.");
            }

            foreach (string path in new[]
            {
                "/i/status/123", "/i/web/status/123", "/intent/tweet/123", "/share/123", "/search/status/123",
                "/hashtag/status/123", "/hamster", "/hamster/status", "/hamster/status/abc", "/hamster/status/123/photo/1"
            })
            {
                Assert.AreEqual(0, LinkRewriter.Rewrite($"https://x.com{path}", mapping, 5).Count, $"'{path}' is not a canonical post path.");
            }

            CollectionAssert.AreEqual(
                new[] { "https://redditez.com/r/dotnet/comments/abc123/a_title/" },
                LinkRewriter.Rewrite("https://reddit.com/r/dotnet/comments/abc123/a_title/", mapping, 5).ToList());
            Assert.AreEqual(1, LinkRewriter.Rewrite("https://reddit.com/r/dotnet/comments/abc123", mapping, 5).Count, "The bare comments path qualifies too.");

            // The app's share form: the token is opaque to the bot but the mirror resolves it, so
            // the rewrite stays a host swap.
            CollectionAssert.AreEqual(
                new[] { "https://redditez.com/r/CombatFootage/s/2k7EBLBrdV" },
                LinkRewriter.Rewrite("https://www.reddit.com/r/CombatFootage/s/2k7EBLBrdV", mapping, 5).ToList());
            Assert.AreEqual(1, LinkRewriter.Rewrite("https://reddit.com/r/dotnet/s/abc123/", mapping, 5).Count, "A trailing slash keeps the share form eligible.");

            foreach (string path in new[] { "/r/dotnet", "/r/dotnet/comments", "/r/dotnet/s", "/r/dotnet/x/abc123", "/user/hamster", "/dotnet/comments/abc123" })
            {
                Assert.AreEqual(0, LinkRewriter.Rewrite($"https://reddit.com{path}", mapping, 5).Count, $"'{path}' is not a thread path.");
            }

            // Short and opaque shapes are never resolved: resolving them would mean fetching.
            Assert.AreEqual(0, LinkRewriter.Rewrite("https://redd.it/abc123", mapping, 5).Count);
            Assert.AreEqual(0, LinkRewriter.Rewrite("https://t.co/abc123", mapping, 5).Count);
        }

        [TestMethod]
        public void Rewrite_Dedupes_On_The_Rewritten_Url_And_Keeps_First_Appearance_Order()
        {
            LinkMapping mapping = LinkMapping.Shipped;
            const string content =
                "https://x.com/hamster/status/2 https://www.x.com/hamster/status/1 https://twitter.com/hamster/status/1 https://reddit.com/r/dotnet/comments/abc";

            CollectionAssert.AreEqual(
                new[]
                {
                    "https://fxtwitter.com/hamster/status/2",
                    "https://fxtwitter.com/hamster/status/1",
                    "https://redditez.com/r/dotnet/comments/abc"
                },
                LinkRewriter.Rewrite(content, mapping, 5).ToList());
        }

        [TestMethod]
        public void Rewrite_Drops_The_Tail_Past_The_Per_Message_Cap()
        {
            LinkMapping mapping = LinkMapping.Shipped;
            const string threeLinks = "https://x.com/a/status/1 https://x.com/a/status/2 https://x.com/a/status/3";

            IReadOnlyList<string> capped = LinkRewriter.Rewrite(threeLinks, mapping, 2);

            CollectionAssert.AreEqual(
                new[] { "https://fxtwitter.com/a/status/1", "https://fxtwitter.com/a/status/2" },
                capped.ToList(),
                "The surplus is dropped and the first N still post.");
        }

        [TestMethod]
        public void Rewrite_Collapses_Identical_Links_Even_When_They_Are_Long()
        {
            LinkMapping mapping = LinkMapping.Shipped;
            string longPost = "https://x.com/a/status/" + new string('1', 900);

            IReadOnlyList<string> links = LinkRewriter.Rewrite($"{longPost} {longPost} {longPost}", mapping, 5);

            Assert.AreEqual(1, links.Count, "Duplicates collapse to one line.");
        }

        [TestMethod]
        public void Rewrite_Drops_The_Tail_That_Would_Exceed_Discords_Message_Limit()
        {
            LinkMapping mapping = LinkMapping.Shipped;
            string longA = "https://x.com/a/status/" + new string('1', 900);
            string longB = "https://x.com/a/status/" + new string('2', 900);
            string longC = "https://x.com/a/status/" + new string('3', 900);

            IReadOnlyList<string> trimmed = LinkRewriter.Rewrite($"{longA} {longB} {longC}", mapping, 5);

            Assert.AreEqual(2, trimmed.Count, "One message, never split: the tail that would exceed 2000 characters is dropped.");
            Assert.IsTrue(trimmed.Sum(url => url.Length) + trimmed.Count - 1 <= LinkRewriter.MaxRepostLength);
        }

        // --- mapping layering ---

        [TestMethod]
        public void Mapping_Ships_The_Three_Origins_With_Their_Default_Mirrors()
        {
            LinkMapping mapping = LinkMapping.Shipped;

            Assert.AreEqual(3, mapping.RowCount);
            CollectionAssert.AreEqual(
                new[] { "reddit.com", "twitter.com", "x.com" },
                mapping.Entries.Select(entry => entry.OriginHost).ToList(),
                "The listing is ordered by origin host.");
            Assert.IsTrue(mapping.Entries.All(entry => entry.Layer == LinkMappingLayer.ShippedDefault));
            Assert.AreEqual("fxtwitter.com", mapping.Entries.Single(entry => entry.OriginHost == "x.com").MirrorHost);
            Assert.AreEqual(LinkPathShape.RedditThread, mapping.Entries.Single(entry => entry.OriginHost == "reddit.com").Shape);
            Assert.IsTrue(mapping.IsKnownMirrorHost("fxtwitter.com"));
            Assert.IsTrue(mapping.IsKnownMirrorHost("redditez.com"));
        }

        [TestMethod]
        public async Task Mapping_Override_Wins_And_Clear_Restores_The_Shipped_Default()
        {
            var harness = new Harness();

            Assert.AreEqual(LinkMappingUpdateStatus.Applied, await harness.Store.SetAsync("reddit.com", "rxddit.com"));

            LinkMapping overridden = await harness.Store.GetAsync();
            LinkMappingEntry reddit = overridden.Entries.Single(entry => entry.OriginHost == "reddit.com");
            Assert.AreEqual("rxddit.com", reddit.MirrorHost);
            Assert.AreEqual(LinkMappingLayer.Runtime, reddit.Layer, "The overridden row is labelled runtime.");
            Assert.IsTrue(overridden.IsKnownMirrorHost("rxddit.com"), "A mirror host of an override is known too.");
            Assert.IsTrue(overridden.IsKnownMirrorHost("fxtwitter.com"), "The shipped mirrors stay known.");

            Assert.AreEqual(LinkMappingUpdateStatus.Cleared, await harness.Store.ClearAsync("reddit.com"));

            LinkMapping restored = await harness.Store.GetAsync();
            Assert.AreEqual("redditez.com", restored.Entries.Single(entry => entry.OriginHost == "reddit.com").MirrorHost);
            Assert.AreEqual(LinkMappingLayer.ShippedDefault, restored.Entries.Single(entry => entry.OriginHost == "reddit.com").Layer);
        }

        [TestMethod]
        public async Task Mapping_Override_Only_Origin_Appears_With_Any_Path_Shape_And_Disappears_On_Clear()
        {
            var harness = new Harness();

            Assert.AreEqual(LinkMappingUpdateStatus.Applied, await harness.Store.SetAsync("example.social", "mirror.social"));

            LinkMapping mapping = await harness.Store.GetAsync();
            Assert.AreEqual(4, mapping.RowCount);
            LinkMappingEntry added = mapping.Entries.Single(entry => entry.OriginHost == "example.social");
            Assert.AreEqual(LinkPathShape.Any, added.Shape, "The bot knows nothing about an operator-added site, so every path qualifies.");

            CollectionAssert.AreEqual(
                new[] { "https://mirror.social/anything/at/all?q=1" },
                LinkRewriter.Rewrite("https://example.social/anything/at/all?q=1", mapping, 5).ToList());

            Assert.AreEqual(LinkMappingUpdateStatus.Cleared, await harness.Store.ClearAsync("example.social"));
            Assert.AreEqual(3, (await harness.Store.GetAsync()).RowCount);
            Assert.AreEqual(LinkMappingUpdateStatus.NothingToClear, await harness.Store.ClearAsync("example.social"));
        }

        [TestMethod]
        public async Task Mapping_Ignores_A_Stored_Row_That_Is_Not_A_Canonical_Hostname()
        {
            var harness = new Harness();
            harness.Database.LinkFixMappings.Add(new DatabaseContext.LinkFixMapping
            {
                OriginHost = "https://x.com/path",
                MirrorHost = "evil.example"
            });
            harness.Database.LinkFixMappings.Add(new DatabaseContext.LinkFixMapping
            {
                OriginHost = "X.COM",
                MirrorHost = "VXTWITTER.COM"
            });
            harness.Database.SaveChanges();

            LinkMapping mapping = await harness.Store.GetAsync();

            Assert.AreEqual(3, mapping.RowCount, "A hand-edited row can never inject a scheme, path, or a second uncleable key.");
            Assert.IsFalse(mapping.IsKnownMirrorHost("evil.example"));
            Assert.AreEqual("fxtwitter.com", mapping.Entries.Single(entry => entry.OriginHost == "x.com").MirrorHost);
        }

        [TestMethod]
        public async Task Mapping_Refuses_A_Mirror_Host_That_Is_A_Known_Origin()
        {
            var harness = new Harness();
            var command = new LinkFixMapCommand(harness.Store, harness.Settings);

            var replies = await ExecuteAsync(command, harness.Database, "!linkfixmap twitter.com x.com");

            StringAssert.Contains(replies[0], "'x.com' is an origin of the current mapping");
            Assert.AreEqual(0, harness.Database.LinkFixMappings.Count(), "Nothing is persisted for a refused mapping.");
            CollectionAssert.AreEqual(
                new[] { "https://fxtwitter.com/hamster/status/1" },
                LinkRewriter.Rewrite("https://x.com/hamster/status/1", await harness.Store.GetAsync(), 5).ToList(),
                "The origin still rewrites to its own mirror.");
        }

        [TestMethod]
        public async Task Mapping_Refuses_An_Origin_That_Is_A_Mirror_Host()
        {
            var harness = new Harness();

            Assert.AreEqual(LinkMappingUpdateStatus.OriginIsMirrorHost, await harness.Store.SetAsync("fxtwitter.com", "x.com"));
            Assert.AreEqual(LinkMappingUpdateStatus.OriginIsMirrorHost, await harness.Store.SetAsync("redditez.com", "rxddit.com"));
            Assert.AreEqual(0, harness.Database.LinkFixMappings.Count(), "Nothing is persisted for a refused origin.");
        }

        [TestMethod]
        public async Task Mapping_Cap_Rejects_New_Origins_But_Still_Updates_Existing_Rows()
        {
            var harness = new Harness(new LinkFixSettings { MaxMappings = 3 });

            Assert.AreEqual(LinkMappingUpdateStatus.CapReached, await harness.Store.SetAsync("example.social", "mirror.social"));
            Assert.AreEqual(LinkMappingUpdateStatus.Applied, await harness.Store.SetAsync("reddit.com", "vxreddit.com"),
                "An existing row still updates past the cap.");
            Assert.AreEqual(1, harness.Database.LinkFixMappings.Count());
        }

        [TestMethod]
        public async Task Mapping_Overrides_Persist_Across_A_Restart()
        {
            var harness = new Harness();
            await harness.Store.SetAsync("twitter.com", "fixupx.com");

            // A new store over the same database is what a restart is: the snapshot is in-memory only.
            var restarted = new LinkMappingStore(ScopeFactoryFor(harness.Database), harness.Settings, NullLogger<LinkMappingStore>.Instance);
            LinkMapping mapping = await restarted.GetAsync();

            Assert.AreEqual("fixupx.com", mapping.Entries.Single(entry => entry.OriginHost == "twitter.com").MirrorHost);
        }

        [TestMethod]
        public async Task Mapping_A_Change_Applies_From_The_Next_Message()
        {
            var harness = new Harness();
            OptIn(harness.Database);

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "https://reddit.com/r/dotnet/comments/abc"));
            StringAssert.Contains(harness.Sends[0].Text, "https://redditez.com/r/dotnet/comments/abc");

            await harness.Store.SetAsync("reddit.com", "rxddit.com");

            harness.Sends.Clear();
            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends, channelId: ChannelId + 1).Object, "https://reddit.com/r/dotnet/comments/abc"));
            StringAssert.Contains(harness.Sends[0].Text, "https://rxddit.com/r/dotnet/comments/abc");
        }

        // --- listener: skip rules and repost shape ---

        [TestMethod]
        public async Task Repost_Posts_One_Reply_With_The_Distinct_Links_In_Order_And_No_Mentions()
        {
            var harness = new Harness();
            OptIn(harness.Database);
            var channel = CreateChannel(harness.Sends);

            await harness.Service.HandleMessageAsync(CreateMessage(channel.Object,
                "hey <@42> https://x.com/hamster/status/2 and https://www.x.com/hamster/status/2 and https://reddit.com/r/dotnet/comments/abc"));

            Assert.AreEqual(1, harness.Sends.Count, "Exactly one follow-up per qualifying message.");
            Assert.AreEqual("https://fxtwitter.com/hamster/status/2\nhttps://redditez.com/r/dotnet/comments/abc", harness.Sends[0].Text);
            AssertNoMentions(harness.Sends[0].AllowedMentions);
            MessageReference reference = harness.Sends[0].Reference!;
            Assert.IsTrue(reference.MessageId.IsSpecified, "The repost replies to the triggering message.");
            Assert.AreEqual(MessageId, reference.MessageId.Value);
            Assert.AreEqual(ChannelId, reference.ChannelId);
            Assert.IsTrue(reference.FailIfNotExists.IsSpecified);
            Assert.IsFalse(reference.FailIfNotExists.Value, "A deleted original must not fail the repost.");
        }

        [TestMethod]
        public async Task Repost_Skips_Bot_Webhook_And_Command_Messages()
        {
            var harness = new Harness();
            OptIn(harness.Database);

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "https://x.com/a/status/1", isBot: true));
            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "https://x.com/a/status/1", isWebhook: true));
            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "!linkfix on https://x.com/a/status/1"));
            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "!quote https://x.com/a/status/1"));

            Assert.AreEqual(0, harness.Sends.Count);
        }

        [TestMethod]
        public async Task Repost_Skips_Direct_Messages_And_Guilds_That_Did_Not_Opt_In()
        {
            var harness = new Harness();
            OptIn(harness.Database, GuildId);
            OptIn(harness.Database, OtherGuildId, enabled: false);

            var directMessages = new List<string>();
            Mock<IMessage> direct = DiscordTestDoubles.CreateDirectMessage("https://x.com/a/status/1", AuthorId, directMessages);
            await harness.Service.HandleMessageAsync(direct.Object);

            Assert.AreEqual(0, directMessages.Count, "DMs are never scanned or answered.");

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends, guildId: OtherGuildId).Object, "https://x.com/a/status/1"));
            Assert.AreEqual(0, harness.Sends.Count, "A guild that has not opted in is never scanned.");

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends, guildId: 43).Object, "https://x.com/a/status/1"));
            Assert.AreEqual(0, harness.Sends.Count, "A guild with no settings row is not opted in either.");
        }

        [TestMethod]
        public async Task Repost_Skips_Already_Mirrored_Links_And_Messages_Without_A_Matching_Host()
        {
            var harness = new Harness();
            OptIn(harness.Database);

            foreach (string content in new[]
            {
                "https://fxtwitter.com/a/status/1",
                "https://redditez.com/r/dotnet/comments/abc",
                "https://example.com/a/status/1",
                "https://x.com/i/status/1",
                "https://redd.it/abc",
                "just chatting, no links"
            })
            {
                await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, content));
            }

            Assert.AreEqual(0, harness.Sends.Count, "A message whose only URLs already point at a mirror (or at nothing) is dropped.");
        }

        [TestMethod]
        public async Task Repost_Respects_The_Per_Message_Cap()
        {
            var harness = new Harness(new LinkFixSettings { MaxLinksPerMessage = 2 });
            OptIn(harness.Database);

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object,
                "https://x.com/a/status/1 https://x.com/a/status/2 https://x.com/a/status/3"));

            Assert.AreEqual(1, harness.Sends.Count);
            Assert.AreEqual(2, harness.Sends[0].Text.Split('\n').Length, "The surplus is dropped, the first N still post.");
        }

        [TestMethod]
        public async Task Repost_Enforces_The_Channel_Cooldown_Until_The_Window_Passes()
        {
            var harness = new Harness(new LinkFixSettings { ChannelCooldown = TimeSpan.FromSeconds(60) });
            OptIn(harness.Database);
            var channel = CreateChannel(harness.Sends);

            await harness.Service.HandleMessageAsync(CreateMessage(channel.Object, "https://x.com/a/status/1"));
            await harness.Service.HandleMessageAsync(CreateMessage(channel.Object, "https://x.com/a/status/2"));
            Assert.AreEqual(1, harness.Sends.Count, "The channel's cooldown window is still running.");

            harness.Time.Advance(TimeSpan.FromSeconds(60));
            await harness.Service.HandleMessageAsync(CreateMessage(channel.Object, "https://x.com/a/status/3"));
            Assert.AreEqual(2, harness.Sends.Count, "Once the window has passed, reposts resume.");
        }

        [TestMethod]
        public async Task Repost_Keeps_A_Separate_Cooldown_Per_Channel()
        {
            var harness = new Harness(new LinkFixSettings { ChannelCooldown = TimeSpan.FromSeconds(60) });
            OptIn(harness.Database);

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "https://x.com/a/status/1"));
            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends, channelId: ChannelId + 1).Object, "https://x.com/a/status/2"));

            Assert.AreEqual(2, harness.Sends.Count, "Another channel keeps its own window.");
        }

        [TestMethod]
        public async Task Repost_With_Zero_Cooldown_Reposts_Every_Message()
        {
            var harness = new Harness(new LinkFixSettings { ChannelCooldown = TimeSpan.Zero });
            OptIn(harness.Database);
            var channel = CreateChannel(harness.Sends);

            await harness.Service.HandleMessageAsync(CreateMessage(channel.Object, "https://x.com/a/status/1"));
            await harness.Service.HandleMessageAsync(CreateMessage(channel.Object, "https://x.com/a/status/2"));

            Assert.AreEqual(2, harness.Sends.Count, "Zero disables the cooldown entirely.");
        }

        [TestMethod]
        public async Task Repost_Has_No_Cooldown_State_After_A_Restart()
        {
            var harness = new Harness(new LinkFixSettings { ChannelCooldown = TimeSpan.FromSeconds(60) });
            OptIn(harness.Database);
            var channel = CreateChannel(harness.Sends);

            await harness.Service.HandleMessageAsync(CreateMessage(channel.Object, "https://x.com/a/status/1"));
            await harness.NewService().HandleMessageAsync(CreateMessage(channel.Object, "https://x.com/a/status/2"));

            Assert.AreEqual(2, harness.Sends.Count, "The cooldown lives in memory only.");
        }

        [TestMethod]
        public async Task Repost_Hands_The_Cooldown_Window_Back_When_The_Send_Fails()
        {
            var harness = new Harness(new LinkFixSettings { ChannelCooldown = TimeSpan.FromSeconds(60) });
            OptIn(harness.Database);

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends, failSends: true).Object, "https://x.com/a/status/1"));
            Assert.AreEqual(0, harness.Sends.Count);

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "https://x.com/a/status/2"));
            Assert.AreEqual(1, harness.Sends.Count, "A repost that never posted does not consume the window.");
        }

        [TestMethod]
        public async Task Repost_Pauses_During_Maintenance_Mode_Without_Queueing()
        {
            var harness = new Harness();
            OptIn(harness.Database);
            harness.Maintenance.Enter();

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "https://x.com/a/status/1"));
            Assert.AreEqual(0, harness.Sends.Count);

            harness.Maintenance.Exit();
            Assert.AreEqual(0, harness.Sends.Count, "Nothing is queued for after the restore.");

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "https://x.com/a/status/1"));
            Assert.AreEqual(1, harness.Sends.Count);
        }

        [TestMethod]
        public async Task Repost_Swallows_A_Failed_Send_And_Logs_No_Content()
        {
            var harness = new Harness();
            OptIn(harness.Database);

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends, failSends: true).Object, "https://x.com/hamster/status/1"));

            Assert.AreEqual(0, harness.Sends.Count);
            Assert.IsTrue(harness.Logger.Messages.Count > 0, "The failure is logged.");
            foreach (string message in harness.Logger.Messages)
            {
                Assert.IsFalse(message.Contains("x.com", StringComparison.OrdinalIgnoreCase), "No host or URL reaches the log.");
                Assert.IsFalse(message.Contains("http", StringComparison.OrdinalIgnoreCase), "No URL reaches the log.");
                Assert.IsFalse(message.Contains("hamster", StringComparison.OrdinalIgnoreCase), "No message text reaches the log.");
            }
        }

        [TestMethod]
        public async Task Repost_Persists_Nothing_Beyond_The_Opt_In_And_Logs_No_Content()
        {
            var harness = new Harness();
            OptIn(harness.Database);

            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object,
                "https://x.com/hamster/status/1 https://reddit.com/r/dotnet/comments/abc"));

            Assert.AreEqual(1, harness.Sends.Count);
            Assert.AreEqual(0, harness.Database.LinkFixMappings.Count(), "No link is ever stored.");
            Assert.AreEqual(1, harness.Database.GuildSettings.Count(), "Only the opt-in row exists.");
            Assert.IsTrue(harness.Database.GuildSettings.Single().LinkFixEnabled);

            foreach (string message in harness.Logger.Messages)
            {
                Assert.IsFalse(message.Contains("http", StringComparison.OrdinalIgnoreCase), "No URL reaches the log.");
                Assert.IsFalse(message.Contains("hamster", StringComparison.OrdinalIgnoreCase), "No message text reaches the log.");
            }
        }

        // --- commands ---

        private static async Task<List<string>> ExecuteAsync(ICommand command, DatabaseContext database, string content, bool directMessage = false,
            ulong guildId = GuildId, ulong authorId = AuthorId)
        {
            var sentMessages = new List<string>();
            var message = directMessage
                ? DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages)
                : DiscordTestDoubles.CreateGuildMessage(content, authorId, guildOwnerId: 100, sentMessages, guildId);
            var context = DiscordTestDoubles.CreateContext(database, guildOwnerId: 100, guildId: directMessage ? null : guildId);
            await command.ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        [TestMethod]
        public void Commands_Declare_Their_Tiers_And_Slash_Declarations()
        {
            var linkFix = new LinkFixCommand();
            var linkFixMap = new LinkFixMapCommand();

            Assert.AreEqual(PermissionTier.Admin, linkFix.DefaultPermissionTier);
            Assert.AreEqual(PermissionTier.BotOwner, linkFixMap.DefaultPermissionTier);
            Assert.AreEqual(CommandCategory.PermissionsAndSettings, linkFix.Category);
            Assert.AreEqual(CommandCategory.PermissionsAndSettings, linkFixMap.Category);
            Assert.IsNotNull(linkFix.ApplicationCommand, "linkfix is slash-eligible.");
            Assert.IsNotNull(linkFixMap.ApplicationCommand, "linkfixmap is slash-eligible.");
        }

        [TestMethod]
        public async Task LinkFixCommand_Shows_And_Toggles_The_Opt_In()
        {
            var harness = new Harness();
            var command = new LinkFixCommand();

            var show = await ExecuteAsync(command, harness.Database, "!linkfix");
            StringAssert.Contains(show[0], "OFF", "The default is off.");

            var enabled = await ExecuteAsync(command, harness.Database, "!linkfix on");
            StringAssert.Contains(enabled[0], "enabled");
            Assert.IsTrue(harness.Database.GuildSettings.Single().LinkFixEnabled);

            var showAgain = await ExecuteAsync(command, harness.Database, "!linkfix");
            StringAssert.Contains(showAgain[0], "ON");

            var disabled = await ExecuteAsync(command, harness.Database, "!linkfix off");
            StringAssert.Contains(disabled[0], "disabled");
            Assert.IsFalse(harness.Database.GuildSettings.Single().LinkFixEnabled);
        }

        [TestMethod]
        public async Task LinkFixCommand_Declines_In_Direct_Messages()
        {
            var harness = new Harness();

            var direct = await ExecuteAsync(new LinkFixCommand(), harness.Database, "!linkfix on", directMessage: true);

            StringAssert.Contains(direct[0], "per-server");
            Assert.AreEqual(0, harness.Database.GuildSettings.Count(), "A direct message writes no guild state.");
        }

        [TestMethod]
        public async Task LinkFixCommand_Reports_Usage_For_Anything_But_On_Or_Off()
        {
            var harness = new Harness();
            var command = new LinkFixCommand();

            foreach (string invocation in new[] { "!linkfix maybe", "!linkfix on off" })
            {
                var replies = await ExecuteAsync(command, harness.Database, invocation);
                StringAssert.Contains(replies[0], "Usage: !linkfix [on|off]");
            }

            Assert.AreEqual(0, harness.Database.GuildSettings.Count(), "A usage error writes no guild state.");
        }

        [TestMethod]
        public async Task LinkFixMapCommand_Lists_The_Effective_Table_With_Layers_And_Row_Count()
        {
            var harness = new Harness();
            await harness.Store.SetAsync("twitter.com", "fixupx.com");
            var command = new LinkFixMapCommand(harness.Store, harness.Settings);

            var listed = await ExecuteAsync(command, harness.Database, "!linkfixmap");

            StringAssert.Contains(listed[0], "3/50 rows");
            StringAssert.Contains(listed[0], "reddit.com → redditez.com (shipped default)");
            StringAssert.Contains(listed[0], "twitter.com → fixupx.com (runtime)");
            StringAssert.Contains(listed[0], "x.com → fxtwitter.com (shipped default)");
        }

        [TestMethod]
        public async Task LinkFixMapCommand_Sets_An_Origin_And_Clears_It_Back_To_The_Shipped_Default()
        {
            var harness = new Harness();
            var command = new LinkFixMapCommand(harness.Store, harness.Settings);

            var set = await ExecuteAsync(command, harness.Database, "!linkfixmap x.com vxtwitter.com");
            StringAssert.Contains(set[0], "x.com → vxtwitter.com (runtime)");
            Assert.AreEqual("vxtwitter.com", harness.Database.LinkFixMappings.Single().MirrorHost, "The override is persisted as a row.");

            OptIn(harness.Database);
            await harness.Service.HandleMessageAsync(CreateMessage(CreateChannel(harness.Sends).Object, "https://x.com/a/status/1"));
            StringAssert.Contains(harness.Sends[0].Text, "https://vxtwitter.com/a/status/1", "The write is effective from the next message.");

            var cleared = await ExecuteAsync(command, harness.Database, "!linkfixmap clear x.com");
            StringAssert.Contains(cleared[0], "shipped default (fxtwitter.com) applies again");
            Assert.AreEqual(0, harness.Database.LinkFixMappings.Count());

            var nothingToClear = await ExecuteAsync(command, harness.Database, "!linkfixmap clear x.com");
            StringAssert.Contains(nothingToClear[0], "No runtime override for x.com");
        }

        [TestMethod]
        public async Task LinkFixMapCommand_Adds_And_Removes_An_Override_Only_Origin()
        {
            var harness = new Harness();
            var command = new LinkFixMapCommand(harness.Store, harness.Settings);

            var added = await ExecuteAsync(command, harness.Database, "!linkfixmap example.social mirror.social");
            StringAssert.Contains(added[0], "example.social → mirror.social (runtime)");

            var removed = await ExecuteAsync(command, harness.Database, "!linkfixmap clear example.social");
            StringAssert.Contains(removed[0], "no longer mapped");
        }

        [TestMethod]
        public async Task LinkFixMapCommand_Rejects_Anything_But_Bare_Hostnames_And_Mirror_Origins()
        {
            var harness = new Harness();
            var command = new LinkFixMapCommand(harness.Store, harness.Settings);

            foreach (string badOrigin in new[] { "https://x.com", "x.com/path", "x.com:443", "user@x.com", "*.x.com", "x" })
            {
                var replies = await ExecuteAsync(command, harness.Database, $"!linkfixmap {badOrigin} fxtwitter.com");
                StringAssert.Contains(replies[0], "is not a bare hostname for the origin");
            }

            var badMirror = await ExecuteAsync(command, harness.Database, "!linkfixmap x.com https://fxtwitter.com");
            StringAssert.Contains(badMirror[0], "is not a bare hostname for the mirror");

            var mirrorOrigin = await ExecuteAsync(command, harness.Database, "!linkfixmap fxtwitter.com x.com");
            StringAssert.Contains(mirrorOrigin[0], "is a mirror host of the current mapping");

            var usage = await ExecuteAsync(command, harness.Database, "!linkfixmap x.com");
            StringAssert.Contains(usage[0], "Usage: !linkfixmap");

            Assert.AreEqual(0, harness.Database.LinkFixMappings.Count(), "A rejected argument writes nothing.");
        }

        [TestMethod]
        public async Task LinkFixMapCommand_Is_Usable_In_Direct_Messages()
        {
            var harness = new Harness();

            var direct = await ExecuteAsync(new LinkFixMapCommand(harness.Store, harness.Settings), harness.Database,
                "!linkfixmap clear reddit.com", directMessage: true);

            StringAssert.Contains(direct[0], "No runtime override for reddit.com");
        }

        [TestMethod]
        public async Task LinkFixMapCommand_Without_A_Store_Reports_Not_Configured()
        {
            var replies = await ExecuteAsync(new LinkFixMapCommand(), new Harness().Database, "!linkfixmap");

            StringAssert.Contains(replies[0], LinkFixMapCommand.NotConfigured);
        }

        [TestMethod]
        public async Task LinkFixMapCommand_Enforces_The_Mapping_Cap()
        {
            var harness = new Harness(new LinkFixSettings { MaxMappings = 3 });
            var command = new LinkFixMapCommand(harness.Store, harness.Settings);

            var rejected = await ExecuteAsync(command, harness.Database, "!linkfixmap example.social mirror.social");
            StringAssert.Contains(rejected[0], "mapping table is full");

            var updated = await ExecuteAsync(command, harness.Database, "!linkfixmap reddit.com rxddit.com");
            StringAssert.Contains(updated[0], "reddit.com → rxddit.com (runtime)");
        }
    }
}
