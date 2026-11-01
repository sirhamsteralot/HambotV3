namespace HambotV2.Connections
{
    /// <summary>
    /// The single upsert path for a guild's <see cref="DatabaseContext.GuildSetting"/> row:
    /// a key lookup (<c>FindAsync</c>) — which also sees a row that is already tracked but not
    /// yet saved, unlike a query — and adds a fresh row when none exists. The caller saves the
    /// context afterwards.
    /// </summary>
    internal static class GuildSettingStore
    {
        public static async Task<DatabaseContext.GuildSetting> GetOrAddAsync(IDatabaseContext database, ulong guildId)
        {
            var setting = await database.GuildSettings.FindAsync(guildId);
            if (setting is not null)
                return setting;

            setting = new DatabaseContext.GuildSetting { GuildId = guildId };
            database.GuildSettings.Add(setting);
            return setting;
        }
    }
}
