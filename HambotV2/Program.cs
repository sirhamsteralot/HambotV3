using HambotV2.Commands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HambotV2
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // In local development, secrets come from the gitignored appsecrets.json; containers
            // supply them as environment variables (Secrets__BotToken,
            // ConnectionStrings__DefaultConnection). Re-adding the environment provider after the
            // file gives env vars precedence, so a container never depends on the excluded file.
            builder.Configuration.AddJsonFile("appsecrets.json", optional: true);
            builder.Configuration.AddEnvironmentVariables();

            // Serilog replaces the default logging providers; sinks and levels come from the
            // "Serilog" configuration section, so the log level is adjustable without a rebuild.
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(builder.Configuration));

            builder.Services.AddHostedService<DiscordClient>();
            builder.Services.AddTransient<ICommandHandler, CommandHandler>();
            builder.Services.AddTransient<IPermissionsService, PermissionsService>();
            builder.Services.AddSingleton<ICommandService, CommandService>();

            // Singleton so the per-guild command identifiers stay cached for the gateway
            // pre-filter and command parsing.
            builder.Services.AddSingleton<IGuildSettingsService>(serviceProvider =>
                new GuildSettingsService(serviceProvider, builder.Configuration));

            // Singleton so the cooldown timestamps are shared across messages; time comes from
            // an injectable TimeProvider for deterministic tests.
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<ICooldownService, CooldownService>();

            builder.Services.AddDbContext<DatabaseContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")),
                ServiceLifetime.Transient,
                ServiceLifetime.Transient);

            // The command pipeline depends on the IDatabaseContext abstraction; map it onto the
            // same transient DatabaseContext registration.
            builder.Services.AddTransient<IDatabaseContext>(serviceProvider =>
                serviceProvider.GetRequiredService<DatabaseContext>());

            using var host = builder.Build();

            // Both fail fast with a critical log line and a non-zero exit code: a
            // half-configured or schema-less bot is worse than no bot.
            if (!ValidateConfiguration(host) || !ApplyMigrations(host))
                return 1;

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
                using var scope = host.Services.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                database.Database.Migrate();

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
