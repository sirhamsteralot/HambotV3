using Discord;
using Discord.WebSocket;
using HambotV2.Backups;
using HambotV2.Commands;
using HambotV2.Connections;
using HambotV2.Maintenance;
using HambotV2.Reminders;
using HambotV2.Scheduling;
using HambotV2.SlashCommands;
using HambotV2.Watchdog;
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

            // Presence & profile identity (design §3.10): the gateway seam wraps the shared
            // client; the presence service is the single path for activity, bio, avatar, and
            // banner changes.
            builder.Services.AddSingleton<IPresenceGateway>(serviceProvider =>
                new DiscordPresenceGateway(serviceProvider.GetRequiredService<DiscordSocketClient>()));
            builder.Services.AddSingleton<IPresenceService>(serviceProvider => new PresenceService(
                serviceProvider.GetRequiredService<IPresenceGateway>(),
                PresenceStartupConfiguration.TryParse(serviceProvider.GetRequiredService<IConfiguration>(), out PresenceStartupConfiguration? startup, out _)
                    ? startup
                    : null,
                serviceProvider.GetRequiredService<ILogger<PresenceService>>()));
            builder.Services.AddSingleton<IImageContentDownloader, ImageContentDownloader>();

            // The gateway client is shared by the Discord hosted service and the reminder DM
            // sender; the container owns it and disposes it after the hosted services stop.
            builder.Services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
            {
                // Only request the intents the bot actually uses; AllUnprivileged would make
                // Discord.Net warn about unused intents at every connect (see DiscordClient).
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent
            }));

            // Scheduled message delivery (§3.8): the generic engine is wired to one message kind
            // at a time — reminders today. The commands (registered through CommandService) and
            // the delivery loop share one wake-up signal, so a schedule change re-schedules
            // immediately; a future scheduled-message kind plugs in with its own source and
            // sender plus another hosted engine on the same signal.
            builder.Services.AddSingleton<IScheduledDeliverySignal, ScheduledDeliverySignal>();
            builder.Services.AddSingleton<IScheduledMessageSource<ScheduledReminderMessage>, ReminderMessageSource>();
            builder.Services.AddSingleton<IScheduledMessageSender<ScheduledReminderMessage>, ReminderDmSender>();
            builder.Services.AddHostedService(serviceProvider => new ScheduledMessageDeliveryService<ScheduledReminderMessage>(
                serviceProvider.GetRequiredService<IScheduledDeliverySignal>(),
                serviceProvider.GetRequiredService<IScheduledMessageSource<ScheduledReminderMessage>>(),
                serviceProvider.GetRequiredService<IScheduledMessageSender<ScheduledReminderMessage>>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ILogger<ScheduledMessageDeliveryService<ScheduledReminderMessage>>>(),
                serviceProvider.GetRequiredService<IMaintenanceMode>(),
                serviceProvider.GetRequiredService<INotificationPublisher>()));

            // Singleton so the per-guild command identifiers stay cached for the gateway
            // pre-filter and command parsing.
            builder.Services.AddSingleton<IGuildSettingsService>(serviceProvider =>
                new GuildSettingsService(serviceProvider, builder.Configuration));

            // Slash surface (design §3.15): the virtual-invocation seam is shared with the planned
            // MCP run_command tool (§3.11); the slash service registers the definitions after the
            // gateway connects and executes interactions through the real pipeline. Opt-in via
            // SlashCommands:Enabled (default false), so Attach() stays a no-op unless enabled.
            builder.Services.AddSingleton<IVirtualCommandRunner, VirtualCommandRunner>();
            builder.Services.AddSingleton<SlashCommandService>();

            // Singleton so the cooldown timestamps are shared across messages; time comes from
            // an injectable TimeProvider for deterministic tests.
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<ICooldownService, CooldownService>();

            // Backups & stability (design §5.2/§3.13):
            // maintenance mode gates the pipeline and the delivery engine during a restore;
            // the backup manager runs pg_dump/pg_restore over the shared backups volume;
            // the notification channel, publisher, health snapshot, and watchdog make
            // degradation loud. All singletons: shared across messages and hosted loops.
            builder.Services.AddSingleton<IMaintenanceMode, MaintenanceMode>();
            builder.Services.AddSingleton<IBackupManager, ProcessBackupManager>();
            builder.Services.AddSingleton<IBackupStatusReader, BackupStatusFileReader>();
            builder.Services.AddSingleton<IGatewayStatus, DiscordGatewayStatus>();
            builder.Services.AddSingleton<INotificationChannelStore, NotificationChannelStore>();
            builder.Services.AddSingleton<IOperatorNotificationSender, DiscordNotificationSender>();
            builder.Services.AddSingleton<INotificationPublisher, NotificationPublisher>();
            builder.Services.AddSingleton<IHealthSnapshotProvider, HealthReport>();
            builder.Services.AddHostedService<WatchdogService>();

            builder.Services.AddDbContext<DatabaseContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")),
                ServiceLifetime.Transient,
                ServiceLifetime.Transient);

            // The command pipeline depends on the IDatabaseContext abstraction; map it onto the
            // same transient DatabaseContext registration.
            builder.Services.AddTransient<IDatabaseContext>(serviceProvider =>
                serviceProvider.GetRequiredService<DatabaseContext>());

            using var host = builder.Build();

            // The version line is what the deploy verification matches (§3.9): it logs before
            // validation so every start, even a misconfigured one, identifies the exact build.
            host.Services.GetRequiredService<ILogger<Program>>()
                .LogInformation("Bot version: {Version}", BotVersion.Current);

            // Both fail fast with a critical log line and a non-zero exit code: a
            // half-configured or schema-less bot is worse than no bot.
            if (!ValidateConfiguration(host) || !ApplyMigrations(host))
                return 1;

            // The presence service subscribes to the gateway's Ready event at construction;
            // resolving it before host.Run() starts the hosted services, so the startup
            // activity and bio cannot miss the event.
            host.Services.GetRequiredService<IPresenceService>();

            host.Run();
            return 0;
        }

        private static bool ValidateConfiguration(IHost host)
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            var missingKeys = ConfigurationValidator.GetMissingKeys(configuration);

            // Optional sections validate their own values; an invalid configured
            // presence or bio fails startup naming the offending key.
            PresenceStartupConfiguration.TryParse(configuration, out _, out List<string> configurationErrors);

            if (missingKeys.Count == 0 && configurationErrors.Count == 0)
                return true;

            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            if (missingKeys.Count > 0)
            {
                logger.LogCritical("Missing required configuration keys: {missingKeys}. " +
                    "Set them in HambotV2/appsecrets.json (local) or via environment variables such as Secrets__BotToken (containers). " +
                    "The bot will not start.", string.Join(", ", missingKeys));
            }
            if (configurationErrors.Count > 0)
            {
                logger.LogCritical("Invalid configuration: {configurationErrors} The bot will not start.",
                    string.Join(" ", configurationErrors));
            }
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
