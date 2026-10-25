using Discord;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace BotTests
{
    /// <summary>
    /// The profile image commands (FR-115/FR-116 / NFR-18) over a faked image downloader and a
    /// mocked presence service. The per-guild apply itself is covered by the service tests in
    /// <see cref="TestPresence"/>.
    /// </summary>
    [TestClass]
    public sealed class TestProfileImages
    {
        private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02];
        private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02];
        private static readonly byte[] GifBytes = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x02];
        private static readonly byte[] NotAnImage = [0x68, 0x65, 0x6C, 0x6C, 0x6F]; // "hello"

        private sealed class FakeImageDownloader : IImageContentDownloader
        {
            public Dictionary<string, byte[]> ImagesByUrl { get; } = [];
            public HashSet<string> FailingUrls { get; } = [];
            public List<string> RequestedUrls { get; } = [];

            public Task<byte[]> DownloadBytesAsync(string url)
            {
                RequestedUrls.Add(url);

                if (FailingUrls.Contains(url))
                    throw new HttpRequestException("simulated download failure");

                return Task.FromResult(ImagesByUrl[url]);
            }
        }

        private sealed class ImageCommandHarness
        {
            public Mock<IPresenceService> Presence { get; } = new();
            public FakeImageDownloader Downloader { get; } = new();
        }

        private static async Task<List<string>> ExecuteAsync(ICommand command, string content, bool directMessage = false, IAttachment[]? attachments = null)
        {
            var sentMessages = new List<string>();

            if (directMessage)
            {
                Assert.IsNull(attachments, "the DM cases run without attachments");
                Mock<IMessage> dm = DiscordTestDoubles.CreateDirectMessage(content, 999, sentMessages);
                var dmContext = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase(), guildId: null);
                await command.ExecuteCommand(dm.Object, dmContext);
                return sentMessages;
            }

            Mock<IMessage> message = attachments is null
                ? DiscordTestDoubles.CreateGuildMessage(content, 999, 999, sentMessages)
                : DiscordTestDoubles.CreateGuildMessage(content, 999, 999, sentMessages, attachments);

            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase(), guildId: DiscordTestDoubles.DefaultGuildId);
            await command.ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        // --- Image validation (FR-115/FR-116) ---

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void TryValidate_Accepts_Png_Jpeg_Gif(int which)
        {
            byte[] image = which == 0 ? PngBytes : which == 1 ? JpegBytes : GifBytes;

            Assert.IsTrue(ProfileImages.TryValidate(image, out string? error));
            Assert.IsNull(error);
        }

        [TestMethod]
        public void TryValidate_Rejects_Non_Image_Content()
        {
            Assert.IsFalse(ProfileImages.TryValidate(NotAnImage, out string? error));
            StringAssert.Contains(error!, "not a PNG, JPEG, or GIF image");
        }

        [TestMethod]
        public void TryValidate_Rejects_Oversized_Images()
        {
            var oversized = new byte[ProfileImages.MaxImageBytes + 1];
            PngBytes.CopyTo(oversized, 0);

            Assert.IsFalse(ProfileImages.TryValidate(oversized, out string? error));
            StringAssert.Contains(error!, "10 MB");
        }

        // --- Registration contracts ---

        [TestMethod]
        public void Avatar_And_Banner_Command_Registration_Contracts()
        {
            var avatar = new AvatarCommand();
            var banner = new BannerCommand();

            Assert.AreEqual("avatar", avatar.Name);
            Assert.AreEqual("banner", banner.Name);
            Assert.IsTrue(avatar.Name == avatar.Name.ToLower());
            Assert.IsTrue(banner.Name == banner.Name.ToLower());
            Assert.IsFalse(string.IsNullOrWhiteSpace(avatar.Description));
            Assert.IsFalse(string.IsNullOrWhiteSpace(avatar.Usage));
            Assert.IsFalse(string.IsNullOrWhiteSpace(banner.Description));
            Assert.IsFalse(string.IsNullOrWhiteSpace(banner.Usage));
            Assert.AreEqual(PermissionTier.BotOwner, avatar.DefaultPermissionTier, "tier 4 (FR-115/FR-116)");
            Assert.AreEqual(PermissionTier.BotOwner, banner.DefaultPermissionTier, "tier 4 (FR-115/FR-116)");

            var commandService = new CommandService();
            commandService.RegisterCommands();
            Assert.IsTrue(commandService.TryGetCommand("!avatar", "!", out _));
            Assert.IsTrue(commandService.TryGetCommand("!banner", "!", out _));
        }

        // --- !avatar (FR-115) ---

        [TestMethod]
        public async Task Avatar_Command_Reports_The_Current_Avatar_Url()
        {
            var harness = new ImageCommandHarness();
            harness.Presence.SetupGet(p => p.CurrentAvatarUrl).Returns("https://cdn.example.test/a.png");
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!avatar");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "https://cdn.example.test/a.png");
        }

        [TestMethod]
        public async Task Avatar_Command_Reports_When_The_Avatar_Is_Unavailable()
        {
            var harness = new ImageCommandHarness();
            harness.Presence.SetupGet(p => p.CurrentAvatarUrl).Returns((string?)null);
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!avatar");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "not available");
        }

        [TestMethod]
        public async Task Avatar_Command_Downloads_And_Applies_The_Single_Attachment()
        {
            var harness = new ImageCommandHarness();
            harness.Downloader.ImagesByUrl["https://cdn.example.test/avatar.png"] = PngBytes;
            harness.Presence.Setup(p => p.ApplyAvatarAsync(It.IsAny<byte[]>())).ReturnsAsync(true);
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);
            var attachment = DiscordTestDoubles.CreateAttachment("https://cdn.example.test/avatar.png", "avatar.png");

            List<string> replies = await ExecuteAsync(command, "!avatar", attachments: [attachment]);

            CollectionAssert.AreEqual(new[] { "https://cdn.example.test/avatar.png" }, harness.Downloader.RequestedUrls,
                "the attachment's CDN URL is the download source");
            harness.Presence.Verify(p => p.ApplyAvatarAsync(PngBytes), Times.Once);
            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "updated");
        }

        [TestMethod]
        public async Task Avatar_Command_Downloads_And_Applies_An_Image_Url_And_Works_In_DMs()
        {
            var harness = new ImageCommandHarness();
            harness.Downloader.ImagesByUrl["https://images.example.test/picture.png"] = PngBytes;
            harness.Presence.Setup(p => p.ApplyAvatarAsync(It.IsAny<byte[]>())).ReturnsAsync(true);
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!avatar https://images.example.test/picture.png", directMessage: true);

            harness.Presence.Verify(p => p.ApplyAvatarAsync(PngBytes), Times.Once);
            Assert.AreEqual(1, replies.Count, "the commands touch no guild state, so they answer in DMs (FR-115)");
            StringAssert.Contains(replies[0], "updated");
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        public async Task Avatar_And_Banner_Commands_Reject_Non_Image_Content(int which)
        {
            var harness = new ImageCommandHarness();
            harness.Downloader.ImagesByUrl["https://images.example.test/file"] = NotAnImage;
            ICommand command = which == 0
                ? new AvatarCommand(harness.Presence.Object, harness.Downloader)
                : new BannerCommand(harness.Presence.Object, harness.Downloader);
            string content = which == 0
                ? "!avatar https://images.example.test/file"
                : "!banner https://images.example.test/file";

            List<string> replies = await ExecuteAsync(command, content);

            StringAssert.Contains(replies[0], "not a PNG, JPEG, or GIF image", "the validation error replies in-channel");
            harness.Presence.Verify(p => p.ApplyAvatarAsync(It.IsAny<byte[]>()), Times.Never, "nothing is applied (FR-115)");
            harness.Presence.Verify(p => p.ApplyBannerAsync(It.IsAny<byte[]>()), Times.Never, "nothing is applied (FR-116)");
        }

        [TestMethod]
        public async Task Avatar_Command_Rejects_Oversized_Images()
        {
            var harness = new ImageCommandHarness();
            var oversized = new byte[ProfileImages.MaxImageBytes + 1];
            PngBytes.CopyTo(oversized, 0);
            harness.Downloader.ImagesByUrl["https://images.example.test/huge.png"] = oversized;
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!avatar https://images.example.test/huge.png");

            StringAssert.Contains(replies[0], "10 MB");
            harness.Presence.Verify(p => p.ApplyAvatarAsync(It.IsAny<byte[]>()), Times.Never);
        }

        [TestMethod]
        public async Task Avatar_Command_Rejects_Ambiguous_Invocations()
        {
            var harness = new ImageCommandHarness();
            harness.Downloader.ImagesByUrl["https://cdn.example.test/avatar.png"] = PngBytes;
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);
            var attachment = DiscordTestDoubles.CreateAttachment("https://cdn.example.test/avatar.png", "avatar.png");

            // Two attachments are ambiguous.
            List<string> twoAttachments = await ExecuteAsync(command, "!avatar", attachments: [attachment, attachment]);
            StringAssert.Contains(twoAttachments[0], "exactly one image");

            // An attachment plus a URL argument is ambiguous too.
            List<string> both = await ExecuteAsync(command, "!avatar https://images.example.test/picture.png", attachments: [attachment]);
            StringAssert.Contains(both[0], "not both");

            harness.Presence.Verify(p => p.ApplyAvatarAsync(It.IsAny<byte[]>()), Times.Never);
        }

        [TestMethod]
        [DataRow("!avatar not-a-url")]
        [DataRow("!avatar ftp://images.example.test/picture.png")]
        [DataRow("!avatar https://images.example.test/a.png extra-token")]
        public async Task Avatar_Command_Rejects_Invalid_Url_Arguments(string content)
        {
            var harness = new ImageCommandHarness();
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, content);

            Assert.AreEqual(0, harness.Downloader.RequestedUrls.Count, "no download is attempted");
            Assert.AreEqual(1, replies.Count);
            harness.Presence.Verify(p => p.ApplyAvatarAsync(It.IsAny<byte[]>()), Times.Never);
        }

        [TestMethod]
        public async Task Avatar_Command_Reports_A_Download_Failure_In_Channel()
        {
            var harness = new ImageCommandHarness();
            harness.Downloader.FailingUrls.Add("https://images.example.test/broken.png");
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!avatar https://images.example.test/broken.png");

            StringAssert.Contains(replies[0], "could not be downloaded");
            harness.Presence.Verify(p => p.ApplyAvatarAsync(It.IsAny<byte[]>()), Times.Never);
        }

        [TestMethod]
        public async Task Avatar_Command_Reset_Removes_The_Custom_Avatar()
        {
            var harness = new ImageCommandHarness();
            harness.Presence.Setup(p => p.ResetAvatarAsync()).ReturnsAsync(true);
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!avatar reset");

            harness.Presence.Verify(p => p.ResetAvatarAsync(), Times.Once);
            StringAssert.Contains(replies[0], "removed");

            // A reset carrying an image is rejected — nothing is downloaded or applied.
            var attachment = DiscordTestDoubles.CreateAttachment("https://cdn.example.test/avatar.png", "avatar.png");
            List<string> withAttachment = await ExecuteAsync(command, "!avatar reset", attachments: [attachment]);
            StringAssert.Contains(withAttachment[0], "reset takes no image");
            Assert.AreEqual(0, harness.Downloader.RequestedUrls.Count);
        }

        [TestMethod]
        public async Task Avatar_Command_Surfaces_An_Apply_Failure_In_Channel()
        {
            var harness = new ImageCommandHarness();
            harness.Downloader.ImagesByUrl["https://images.example.test/avatar.png"] = PngBytes;
            harness.Presence.Setup(p => p.ApplyAvatarAsync(It.IsAny<byte[]>())).ReturnsAsync(false);
            var command = new AvatarCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!avatar https://images.example.test/avatar.png");

            StringAssert.Contains(replies[0], "Failed to update", "apply failures reply in-channel (FR-115)");
        }

        [TestMethod]
        public async Task Avatar_Command_Without_A_Configured_Service_Reports_It()
        {
            var command = new AvatarCommand();

            List<string> replies = await ExecuteAsync(command, "!avatar");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "not configured");
        }

        // --- !banner (FR-116) ---

        [TestMethod]
        public async Task Banner_Command_Reports_The_Current_Banner_Url_Or_That_None_Is_Set()
        {
            var harness = new ImageCommandHarness();
            harness.Presence.SetupGet(p => p.CurrentBannerUrl).Returns("https://cdn.example.test/banner.png");
            var command = new BannerCommand(harness.Presence.Object, harness.Downloader);

            List<string> withBanner = await ExecuteAsync(command, "!banner");
            StringAssert.Contains(withBanner[0], "https://cdn.example.test/banner.png");

            harness.Presence.SetupGet(p => p.CurrentBannerUrl).Returns((string?)null);
            List<string> withoutBanner = await ExecuteAsync(command, "!banner");
            StringAssert.Contains(withoutBanner[0], "No banner is set");
        }

        [TestMethod]
        public async Task Banner_Command_Downloads_And_Applies_The_Image_From_An_Url()
        {
            var harness = new ImageCommandHarness();
            harness.Downloader.ImagesByUrl["https://images.example.test/banner.gif"] = GifBytes;
            harness.Presence.Setup(p => p.ApplyBannerAsync(It.IsAny<byte[]>())).ReturnsAsync(true);
            var command = new BannerCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!banner https://images.example.test/banner.gif");

            harness.Presence.Verify(p => p.ApplyBannerAsync(GifBytes), Times.Once);
            StringAssert.Contains(replies[0], "updated");
        }

        [TestMethod]
        public async Task Banner_Command_Clears_The_Banner()
        {
            var harness = new ImageCommandHarness();
            harness.Presence.Setup(p => p.ClearBannerAsync()).ReturnsAsync(true);
            var command = new BannerCommand(harness.Presence.Object, harness.Downloader);

            List<string> replies = await ExecuteAsync(command, "!banner clear");

            harness.Presence.Verify(p => p.ClearBannerAsync(), Times.Once);
            StringAssert.Contains(replies[0], "removed");
        }
    }
}
