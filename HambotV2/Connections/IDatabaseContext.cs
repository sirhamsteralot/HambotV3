using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HambotV2.Connections
{
    /// <summary>
    /// Persistence abstraction used by the command pipeline (roadmap T6). It exposes only the
    /// members commands and permission checks need, so they can be unit-tested against any
    /// implementation — e.g. the EF InMemory provider — without a reachable database.
    /// </summary>
    internal interface IDatabaseContext : IDisposable
    {
        DbSet<DatabaseContext.User> Users { get; }

        DbSet<DatabaseContext.CommandMetaData> Commands { get; }

        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}
