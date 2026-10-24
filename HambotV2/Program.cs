using HambotV2.Commands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace HambotV2
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            // Secrets come from the gitignored appsecrets.json in local development, and from
            // environment variables (Secrets__BotToken, ConnectionStrings__DefaultConnection) in
            // containers. Re-adding the environment provider after the JSON file gives env vars
            // precedence, so a container never depends on the (excluded) secrets file.
            builder.Configuration.AddJsonFile("appsecrets.json", optional: true);
            builder.Configuration.AddEnvironmentVariables();
            // Serilog replaces the default providers as the logging backend (roadmap T12). All bot
            // code keeps using ILogger<T>; the sinks and levels come from the "Serilog" section of
            // appsettings.json / environment variables, so the log level is adjustable without a
            // rebuild. Everything logged before the host starts (validation, migrations) uses the
            // same configuration because the provider is registered before the host is built.
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(builder.Configuration));
            builder.Services.AddDbContext<DatabaseContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")), ServiceLifetime.Transient, ServiceLifetime.Transient);
            // The command pipeline depends on the IDatabaseContext abstraction (roadmap T6); this maps
            // it onto the same transient DatabaseContext registration.
            builder.Services.AddTransient<IDatabaseContext>(serviceProvider => serviceProvider.GetRequiredService<DatabaseContext>());
            builder.Services.AddHostedService<DiscordClient>();
            builder.Services.AddTransient<ICommandHandler, CommandHandler>();
            builder.Services.AddTransient<IPermissionsService, PermissionsService>();
            builder.Services.AddSingleton<ICommandService, CommandService>();

            var host = builder.Build();

            if (!ValidateConfiguration(host))
            {
                host.Dispose();
                return 1;
            }

            // The schema must exist before the gateway starts; a half-functional bot is worse than no bot.
            if (!ApplyMigrations(host))
            {
                host.Dispose();
                return 1;
            }

            host.Run();
            return 0;
        }

        private static bool ValidateConfiguration(IHost host)
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var missingKeys = ConfigurationValidator.GetMissingKeys(configuration);

            if (missingKeys.Count == 0)
                return true;

            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            logger.LogCritical("Missing required configuration keys: {missingKeys}. " +
                "Set them in HambotV2/appsecrets.json (local) or via environment variables such as Secrets__BotToken (containers). " +
                "The bot will not start.", string.Join(", ", missingKeys));
            return false;
        }

        private static bool ApplyMigrations(IHost host)
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();

            try
            {
                using (var scope = host.Services.CreateScope())
                {
                    var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                    database.Database.Migrate();
                }

                logger.LogInformation("Database migrations applied successfully");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Database is unreachable or the migration failed. " +
                    "Ensure PostgreSQL is reachable and ConnectionStrings:DefaultConnection is configured correctly. " +
                    "The bot will not start.");
                return false;
            }
        }
    }
}
