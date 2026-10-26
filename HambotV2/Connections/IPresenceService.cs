using Discord;

namespace HambotV2.Connections
{
    /// <summary>The bot's activity: one of the four supported types plus its status text.</summary>
    internal sealed record PresenceActivity(ActivityType Type, string Text);

    /// <summary>The limits of the identity surfaces, shared by startup validation, the commands, and tests.</summary>
    internal static class ProfileLimits
    {
        public const int MaxActivityTextLength = 128;
        public const int MaxBioLength = 190;
    }

    /// <summary>The four supported activity types; streaming is out of scope (it needs a stream URL).</summary>
    internal static class PresenceActivityTypes
    {
        public static readonly string[] SupportedNames = ["playing", "watching", "listening", "competing"];

        public static bool TryParse(string token, out ActivityType type)
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "playing":
                    type = ActivityType.Playing;
                    return true;
                case "watching":
                    type = ActivityType.Watching;
                    return true;
                case "listening":
                    type = ActivityType.Listening;
                    return true;
                case "competing":
                    type = ActivityType.Competing;
                    return true;
                default:
                    type = ActivityType.Playing;
                    return false;
            }
        }

        /// <summary>Discord's own phrasing ("listening to X").</summary>
        public static string GetLabel(ActivityType type) => type switch
        {
            ActivityType.Listening => "listening to",
            ActivityType.Playing => "playing",
            ActivityType.Watching => "watching",
            ActivityType.Competing => "competing",
            _ => type.ToString().ToLowerInvariant()
        };
    }

    /// <summary>
    /// The single path for every change to the bot's Discord identity (design §3.10): startup
    /// configuration, the profile commands, and later the LLM job and MCP tools all converge
    /// here. Applies are single-flight with last-write-wins; requests before the gateway is
    /// ready are deferred to its Ready event; a failed apply is logged and returned as false,
    /// never thrown.
    /// </summary>
    internal interface IPresenceService
    {
        /// <summary>The last applied (or deferred) activity, or null when none is set.</summary>
        PresenceActivity? CurrentActivity { get; }

        /// <summary>The last applied (or deferred) bio, or null when none is set.</summary>
        string? CurrentBio { get; }

        string? CurrentAvatarUrl { get; }

        string? CurrentBannerUrl { get; }

        Task<bool> ApplyActivityAsync(ActivityType type, string text);

        Task<bool> ClearActivityAsync();

        /// <summary>Applies the bio in every guild the bot is a member of.</summary>
        Task<bool> ApplyBioAsync(string bio);

        Task<bool> ClearBioAsync();

        /// <summary>Applies the avatar per guild plus globally.</summary>
        Task<bool> ApplyAvatarAsync(byte[] imageData);

        /// <summary>Removes the custom avatar, falling back to the application default.</summary>
        Task<bool> ResetAvatarAsync();

        /// <summary>Applies the banner in every guild the bot is a member of.</summary>
        Task<bool> ApplyBannerAsync(byte[] imageData);

        Task<bool> ClearBannerAsync();
    }
}
