using Microsoft.EntityFrameworkCore;

namespace HambotV2.Connections
{
    /// <summary>
    /// Persistence abstraction used by the command pipeline. It exposes only the
    /// members commands and permission checks need, so they can be unit-tested against any
    /// implementation — e.g. the EF InMemory provider — without a reachable database.
    /// </summary>
    internal interface IDatabaseContext : IDisposable
    {
        DbSet<DatabaseContext.User> Users { get; }

        DbSet<DatabaseContext.CommandMetaData> Commands { get; }

        DbSet<DatabaseContext.GuildSetting> GuildSettings { get; }

        DbSet<DatabaseContext.Quote> Quotes { get; }

        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}
