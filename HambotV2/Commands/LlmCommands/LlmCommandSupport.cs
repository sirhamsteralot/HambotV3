using HambotV2.Llm;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>Shared bits of the LLM commands: the not-configured reply and surface parsing/labels.</summary>
    internal static class LlmCommandSupport
    {
        public const string NotConfigured = "LLM features are not configured on this installation.";

        public static bool TryParseSurface(string? name, out LlmSurface surface)
        {
            switch (name)
            {
                case "presence":
                    surface = LlmSurface.Presence;
                    return true;
                case "bio":
                    surface = LlmSurface.Bio;
                    return true;
                default:
                    surface = default;
                    return false;
            }
        }

        public static string SurfaceLabel(LlmSurface surface) => surface == LlmSurface.Presence ? "Presence" : "Bio";
    }
}
