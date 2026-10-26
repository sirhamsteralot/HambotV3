using Discord;

namespace HambotV2.Connections
{
    /// <summary>
    /// The optional startup configuration: <c>BotConfiguration:Presence</c>
    /// (<c>Type</c> + <c>Text</c>) for the initial activity and <c>BotConfiguration:Bio</c> for
    /// the initial bio. Pure so startup fail-fast (naming the offending key) and the service
    /// share one source of truth, and tests run without a host.
    /// </summary>
    internal sealed record PresenceStartupConfiguration
    {
        /// <summary>The configured initial activity; null means "set no activity".</summary>
        public required PresenceActivity? Activity { get; init; }

        /// <summary>False means "leave the existing bio untouched at startup".</summary>
        public required bool HasBio { get; init; }

        public string Bio { get; init; } = string.Empty;

        /// <summary>
        /// Absent configuration parses successfully to null; invalid values collect errors naming
        /// the offending key. The parsed value is non-null exactly when something is configured.
        /// </summary>
        public static bool TryParse(IConfiguration configuration, out PresenceStartupConfiguration? parsed, out List<string> errors)
        {
            errors = new List<string>();
            parsed = null;

            string? typeToken = configuration["BotConfiguration:Presence:Type"];
            string? activityText = configuration["BotConfiguration:Presence:Text"];
            string? bio = configuration["BotConfiguration:Bio"];

            bool presenceConfigured = !string.IsNullOrWhiteSpace(typeToken) || !string.IsNullOrWhiteSpace(activityText);
            PresenceActivity? activity = null;

            if (presenceConfigured)
            {
                if (string.IsNullOrWhiteSpace(typeToken))
                {
                    errors.Add("BotConfiguration:Presence:Type is required when BotConfiguration:Presence:Text is set.");
                }
                else if (!PresenceActivityTypes.TryParse(typeToken, out ActivityType type))
                {
                    errors.Add($"BotConfiguration:Presence:Type must be one of {string.Join(", ", PresenceActivityTypes.SupportedNames)} (got '{typeToken}').");
                }
                else if (string.IsNullOrWhiteSpace(activityText))
                {
                    errors.Add("BotConfiguration:Presence:Text is required when BotConfiguration:Presence:Type is set.");
                }
                else if (activityText.Length > ProfileLimits.MaxActivityTextLength)
                {
                    errors.Add($"BotConfiguration:Presence:Text must be at most {ProfileLimits.MaxActivityTextLength} characters (got {activityText.Length}).");
                }
                else
                {
                    activity = new PresenceActivity(type, activityText);
                }
            }

            bool hasBio = !string.IsNullOrWhiteSpace(bio);
            if (hasBio && bio!.Length > ProfileLimits.MaxBioLength)
            {
                errors.Add($"BotConfiguration:Bio must be at most {ProfileLimits.MaxBioLength} characters (got {bio.Length}).");
                hasBio = false;
            }

            if (errors.Count > 0)
                return false;

            if (activity is not null || hasBio)
            {
                parsed = new PresenceStartupConfiguration
                {
                    Activity = activity,
                    HasBio = hasBio,
                    Bio = hasBio ? bio! : string.Empty
                };
            }

            return true;
        }
    }
}
