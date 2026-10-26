using HambotV2.Connections;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Manages the bot's profile picture: show URL, set from one attachment or an image
    /// URL (per guild plus globally), `reset` falls back to the application default. Tier 4,
    /// DM-usable, not persisted.
    /// </summary>
    internal sealed class AvatarCommand : ProfileImageCommandBase
    {
        public AvatarCommand(IPresenceService? presenceService = null, IImageContentDownloader? imageDownloader = null)
            : base(presenceService, imageDownloader)
        {
        }

        public override string Name => "avatar";
        public override string Description => "Shows, sets, or resets the bot's profile picture (attach one image or pass an image URL)";
        public override string Usage => "avatar [<image-url> | reset]";

        public override ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show, an image URL, or reset");

        protected override string ResetKeyword => "reset";
        protected override string ImageKind => "avatar";

        protected override Task<bool> ApplyImageAsync(IPresenceService presenceService, byte[] imageData)
            => presenceService.ApplyAvatarAsync(imageData);

        protected override Task<bool> ResetImageAsync(IPresenceService presenceService)
            => presenceService.ResetAvatarAsync();

        protected override string? GetCurrentImageUrl(IPresenceService presenceService)
            => presenceService.CurrentAvatarUrl;

        protected override string NoImageReply => "The avatar is not available right now — the gateway is still connecting.";
        protected override string ResetReply => "The custom avatar was removed — the application default applies.";
        protected override string AppliedReply => "The avatar was updated.";
    }
}
