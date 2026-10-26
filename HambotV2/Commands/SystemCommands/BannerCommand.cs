using HambotV2.Connections;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Manages the bot's profile banner with the same shape as `!avatar`, but per guild
    /// only (bots have no global banner). Tier 4, DM-usable, not persisted.
    /// </summary>
    internal sealed class BannerCommand : ProfileImageCommandBase
    {
        public BannerCommand(IPresenceService? presenceService = null, IImageContentDownloader? imageDownloader = null)
            : base(presenceService, imageDownloader)
        {
        }

        public override string Name => "banner";
        public override string Description => "Shows, sets, or clears the bot's profile banner (attach one image or pass an image URL)";
        public override string Usage => "banner [<image-url> | clear]";

        protected override string ResetKeyword => "clear";
        protected override string ImageKind => "banner";

        protected override Task<bool> ApplyImageAsync(IPresenceService presenceService, byte[] imageData)
            => presenceService.ApplyBannerAsync(imageData);

        protected override Task<bool> ResetImageAsync(IPresenceService presenceService)
            => presenceService.ClearBannerAsync();

        protected override string? GetCurrentImageUrl(IPresenceService presenceService)
            => presenceService.CurrentBannerUrl;

        protected override string NoImageReply => "No banner is set.";
        protected override string ResetReply => "The banner was removed.";
        protected override string AppliedReply => "The banner was updated.";
    }
}
