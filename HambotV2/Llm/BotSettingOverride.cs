using HambotV2.Connections;

namespace HambotV2.Llm
{
    /// <summary>
    /// Persistence of the global runtime LLM overrides (the <c>BotSetting</c> key-value rows):
    /// set/clear helpers shared by the <c>!persona</c> and <c>!model</c> commands and their resolvers.
    /// </summary>
    internal static class BotSettingOverride
    {
        public static string? Get(IDatabaseContext? database, string key) =>
            database?.BotSettings.FirstOrDefault(setting => setting.Key == key)?.Value;

        public static async Task SetAsync(IDatabaseContext database, string key, string value)
        {
            var setting = database.BotSettings.FirstOrDefault(item => item.Key == key);
            if (setting is null)
            {
                setting = new DatabaseContext.BotSetting { Key = key };
                database.BotSettings.Add(setting);
            }

            setting.Value = value;
            await database.SaveChangesAsync();
        }

        public static async Task ClearAsync(IDatabaseContext database, string key)
        {
            var existing = database.BotSettings.FirstOrDefault(item => item.Key == key);
            if (existing is not null)
                database.BotSettings.Remove(existing);
            await database.SaveChangesAsync();
        }
    }
}
