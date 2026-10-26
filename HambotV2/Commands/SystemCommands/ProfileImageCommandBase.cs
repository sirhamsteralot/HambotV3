using System.Diagnostics.CodeAnalysis;
using Discord;
using HambotV2.Connections;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// The shared flow of `!avatar` and `!banner` (FR-115/FR-116): no args reply with the current
    /// image's CDN URL; the new image comes from exactly one attachment or a single image URL,
    /// is downloaded and validated before anything is applied, and the reset keyword removes the
    /// custom image. Tier 4, DM-usable; no startup configuration, nothing persisted.
    /// </summary>
    internal abstract class ProfileImageCommandBase : ICommand
    {
        private readonly IPresenceService? _presenceService;
        private readonly IImageContentDownloader? _imageDownloader;

        protected ProfileImageCommandBase(IPresenceService? presenceService, IImageContentDownloader? imageDownloader)
        {
            _presenceService = presenceService;
            _imageDownloader = imageDownloader;
        }

        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract string Usage { get; }
        public int DefaultPermissionTier => PermissionTier.BotOwner;

        /// <summary>
        /// Slash surface (FR-110): the argument line stays free-form (attachments are a
        /// text-surface input; the URL and reset keyword work on both).
        /// </summary>
        public abstract ApplicationCommandDeclaration? ApplicationCommand { get; }

        /// <summary>The keyword that removes the custom image (avatar: <c>reset</c>, banner: <c>clear</c>).</summary>
        protected abstract string ResetKeyword { get; }

        protected abstract Task<bool> ApplyImageAsync(IPresenceService presenceService, byte[] imageData);

        protected abstract Task<bool> ResetImageAsync(IPresenceService presenceService);

        protected abstract string? GetCurrentImageUrl(IPresenceService presenceService);

        protected abstract string NoImageReply { get; }

        protected abstract string ResetReply { get; }

        protected abstract string AppliedReply { get; }

        /// <summary>The image kind named in errors ("avatar"/"banner").</summary>
        protected abstract string ImageKind { get; }

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_presenceService is null || _imageDownloader is null)
            {
                await message.Channel.SendMessageAsync("Profile image management is not configured on this installation.");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            string? firstArgument = arguments.Get(0);
            IReadOnlyCollection<IAttachment> attachments = message.Attachments;

            if (firstArgument == ResetKeyword)
            {
                if (attachments.Count > 0)
                {
                    await message.Channel.SendMessageAsync($"{ResetKeyword} takes no image — remove the attachment.");
                    return;
                }

                bool reset = await ResetImageAsync(_presenceService);
                await message.Channel.SendMessageAsync(reset
                    ? ResetReply
                    : $"Failed to update the {ImageKind} — a Discord error occurred. Please try again in a moment.");
                return;
            }

            if (string.IsNullOrWhiteSpace(firstArgument) && attachments.Count == 0)
            {
                string? url = GetCurrentImageUrl(_presenceService);
                await message.Channel.SendMessageAsync(url is null ? NoImageReply : $"Current {ImageKind}: {url}");
                return;
            }

            byte[]? imageData = await TryDownloadImageAsync(message, arguments, firstArgument, attachments, _imageDownloader);
            if (imageData is null)
                return; // already replied in-channel

            if (!ProfileImages.TryValidate(imageData, out string? validationError))
            {
                await message.Channel.SendMessageAsync($"The {ImageKind} was not changed — {validationError}.");
                return;
            }

            bool applied = await ApplyImageAsync(_presenceService, imageData);
            await message.Channel.SendMessageAsync(applied
                ? AppliedReply
                : $"Failed to update the {ImageKind} — a Discord error occurred. Please try again in a moment.");
        }

        /// <summary>Downloads from exactly one attachment or a single image URL; every failure replies in-channel and returns null.</summary>
        private async Task<byte[]?> TryDownloadImageAsync(IMessage message, CommandArguments arguments, string? firstArgument, IReadOnlyCollection<IAttachment> attachments, IImageContentDownloader downloader)
        {
            if (attachments.Count > 1)
            {
                await message.Channel.SendMessageAsync("Attach exactly one image — multiple attachments are ambiguous.");
                return null;
            }

            if (attachments.Count == 1)
            {
                if (!string.IsNullOrWhiteSpace(firstArgument))
                {
                    await message.Channel.SendMessageAsync("Pass either one image attachment or a single image URL, not both.");
                    return null;
                }

                return await DownloadAsync(message, attachments.First().Url, downloader,
                    "The attached image could not be downloaded. Try again, or pass an image URL instead.");
            }

            if (arguments.Count > 1)
            {
                await message.Channel.SendMessageAsync("Pass a single image URL as the argument.");
                return null;
            }

            if (!TryCreateHttpUrl(firstArgument!, out Uri? url))
            {
                await message.Channel.SendMessageAsync("That does not look like an image URL — pass the direct link to a PNG, JPEG, or GIF image.");
                return null;
            }

            return await DownloadAsync(message, url.ToString(), downloader,
                "The image could not be downloaded from that URL — check the link and try again.");
        }

        private async Task<byte[]?> DownloadAsync(IMessage message, string url, IImageContentDownloader downloader, string failureReply)
        {
            try
            {
                return await downloader.DownloadBytesAsync(url);
            }
            catch (Exception)
            {
                await message.Channel.SendMessageAsync(failureReply);
                return null;
            }
        }

        private static bool TryCreateHttpUrl(string token, [NotNullWhen(true)] out Uri? url)
        {
            return Uri.TryCreate(token, UriKind.Absolute, out url)
                && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);
        }
    }
}
