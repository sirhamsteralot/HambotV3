using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HambotV2.Connections
{
    /// <summary>
    /// Design-time context for `dotnet ef`: the EF tools use this instead of running
    /// Program.Main, which would attempt a real database migration at startup. The connection string
    /// only needs to be parseable for migration scaffolding — it is never used to connect.
    /// </summary>
    public class DesignTimeDatabaseContextFactory : IDesignTimeDbContextFactory<DatabaseContext>
    {
        public DatabaseContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<DatabaseContext>()
                .UseNpgsql("Host=localhost;Database=hambotv2-design")
                .Options;

            return new DatabaseContext(options);
        }
    }
}
