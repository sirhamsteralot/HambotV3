using HambotV2.Connections;

namespace HambotV2.Llm
{
    /// <summary>
    /// The one layered-resolution pattern behind every <c>BotSetting</c>-backed override:
    /// the runtime override (the <c>BotSetting</c> row set by a command) → the configuration
    /// value → the shipped default, reporting which layer supplied the value. Blank
    /// override/configuration values fall through to the next layer.
    /// </summary>
    internal static class LayeredBotSettingResolver
    {
        public static (string Value, string Layer) Resolve(
            IDatabaseContext? database,
            string overrideKey,
            string overrideLayer,
            string? configuredValue,
            string configuredLayer,
            string defaultValue)
        {
            string? runtimeOverride = BotSettingOverride.Get(database, overrideKey);
            if (!string.IsNullOrWhiteSpace(runtimeOverride))
                return (runtimeOverride, overrideLayer);

            if (!string.IsNullOrWhiteSpace(configuredValue))
                return (configuredValue, configuredLayer);

            return (defaultValue, "shipped default");
        }
    }
}
