using Discord;
using Discord.WebSocket;
using HambotV2.Backups;
using HambotV2.Commands;
using HambotV2.Commands.QuoteCommands;
using HambotV2.Connections;
using HambotV2.Llm;
using HambotV2.Maintenance;
using HambotV2.Mcp;
using HambotV2.QuoteOfTheDay;
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

            // Presence & profile identity: the gateway seam wraps the shared client; the
            // presence service is the single path for activity, bio, avatar, and banner changes.
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

            // Scheduled message delivery: the generic engine is wired to one message kind at a
            // time — reminders today. The commands and the delivery loop share one wake-up
            // signal, so a schedule change re-schedules immediately.
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

            // Quote of the day: the publication loop posts each configured guild's random quote
            // into its channel at the guild's schedule boundaries. The sender resolves the
            // channel by id on the shared gateway client at publication time; a deleted or
            // unavailable channel is a per-boundary failure, not a configuration-time check.
            builder.Services.AddSingleton<IQuoteOfTheDayChannelSender>(serviceProvider =>
                new DiscordQuoteOfTheDayChannelSender(serviceProvider.GetRequiredService<DiscordSocketClient>()));
            builder.Services.AddHostedService<QuoteOfTheDayPublicationService>();

            // Singleton so the per-guild command identifiers stay cached for the gateway
            // pre-filter and command parsing.
            builder.Services.AddSingleton<IGuildSettingsService>(serviceProvider =>
                new GuildSettingsService(serviceProvider, builder.Configuration));

            // Slash surface: the virtual-invocation seam is shared with the MCP run_command
            // tool; the slash service registers the definitions after the gateway connects and
            // executes interactions through the real pipeline. Opt-in via
            // SlashCommands:Enabled (default false), so Attach() stays a no-op unless enabled.
            builder.Services.AddSingleton<IVirtualCommandRunner, VirtualCommandRunner>();
            builder.Services.AddSingleton<SlashCommandService>();

            // The backlog of each invoker's latest quote search: one snapshot per invoker, shared
            // by the search command and the pagination buttons that page through its results.
            builder.Services.AddSingleton<QuoteSearchBacklog>();

            // Interactive pagination: handles the previous/next buttons of every paginated list —
            // help pages through the same virtual-invocation seam as the slash surface, quote
            // search pages through the backlog snapshot. Attached unconditionally — the buttons
            // ride on text-surface messages too.
            builder.Services.AddSingleton<ButtonPaginationService>();

            // Singleton so the cooldown timestamps are shared across messages; time comes from
            // an injectable TimeProvider for deterministic tests.
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<ICooldownService, CooldownService>();

            // Backups & stability: maintenance mode gates the pipeline and the delivery engine
            // during a restore; the backup manager runs pg_dump/pg_restore over the shared
            // backups volume; the notification channel, publisher, health snapshot, and watchdog
            // make degradation loud. All singletons: shared across messages and hosted loops.
            builder.Services.AddSingleton<IMaintenanceMode, MaintenanceMode>();
            builder.Services.AddSingleton<IBackupManager, ProcessBackupManager>();
            builder.Services.AddSingleton<IBackupStatusReader, BackupStatusFileReader>();
            builder.Services.AddSingleton<IGatewayStatus, DiscordGatewayStatus>();
            builder.Services.AddSingleton<INotificationChannelStore, NotificationChannelStore>();
            builder.Services.AddSingleton<IOperatorNotificationSender, DiscordNotificationSender>();
            builder.Services.AddSingleton<INotificationPublisher, NotificationPublisher>();
            builder.Services.AddSingleton<IHealthSnapshotProvider, HealthReport>();
            builder.Services.AddHostedService<WatchdogService>();

            // LLM foundation: settings parsed once from the optional Llm section; the client is
            // registered only when an API key is configured, so without one every LLM feature is
            // inactive. The gate (kill switch + daily budget + accounting), prompt builder, chat
            // context collector, and the presence/bio update loop are singletons/hosted; the
            // update loop stays alive (idle) while no job is enabled so !llm update can trigger
            // manually. One PresenceUpdateService instance serves both the hosted loops and the
            // command's manual trigger.
            var llmSettings = LlmSettings.FromConfiguration(builder.Configuration);
            builder.Services.AddSingleton(llmSettings);
            builder.Services.AddSingleton<ILlmModelResolver, LlmModelResolver>();
            if (!string.IsNullOrWhiteSpace(llmSettings.ApiKey))
            {
                // The client resolves the effective model per call, so a runtime !model override applies without a restart.
                builder.Services.AddSingleton<ILlmClient>(serviceProvider =>
                    new MistralLmClient(llmSettings.ApiKey, serviceProvider.GetRequiredService<ILlmModelResolver>()));
            }
            builder.Services.AddSingleton<ILlmUsageTracker, LlmUsageTracker>();
            // GetService (not GetRequiredService) for the client: without an API key ILlmClient is
            // unregistered and the gate activates with a null client, i.e. IsConfigured == false.
            builder.Services.AddSingleton<ILlmGate>(serviceProvider => new LlmGate(
                serviceProvider.GetService<ILlmClient>(),
                serviceProvider.GetRequiredService<ILlmUsageTracker>(),
                llmSettings,
                serviceProvider.GetRequiredService<ILogger<LlmGate>>()));
            builder.Services.AddSingleton<LlmPromptBuilder>();
            builder.Services.AddSingleton<IChatContextCollector, ChatContextCollector>();
            builder.Services.AddSingleton<PresenceUpdateService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PresenceUpdateService>());
            builder.Services.AddSingleton<ILlmUpdateService>(serviceProvider => serviceProvider.GetRequiredService<PresenceUpdateService>());

            // MCP endpoint: the operator-agent tool surface, opt-in via Mcp:Enabled +
            // Mcp:ApiKey. The narrow Discord seam keeps the tools unit-testable without a
            // live connection; the hosted service starts Kestrel only when active.
            builder.Services.AddSingleton<IMcpDiscordClient>(serviceProvider =>
                new DiscordSocketMcpClient(serviceProvider.GetRequiredService<DiscordSocketClient>()));
            builder.Services.AddSingleton<IMcpTools>(serviceProvider => new McpTools(
                serviceProvider.GetRequiredService<IMcpDiscordClient>(),
                serviceProvider.GetRequiredService<IPresenceService>(),
                serviceProvider.GetRequiredService<IGatewayStatus>(),
                serviceProvider.GetRequiredService<ICommandService>(),
                serviceProvider.GetRequiredService<IVirtualCommandRunner>(),
                serviceProvider.GetRequiredService<IMaintenanceMode>(),
                serviceProvider,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ILogger<McpTools>>()));
            builder.Services.AddSingleton<McpEndpointStatus>();
            builder.Services.AddSingleton<IMcpEndpointStatus>(serviceProvider =>
                serviceProvider.GetRequiredService<McpEndpointStatus>());
            builder.Services.AddHostedService<McpServerService>();

            builder.Services.AddDbContext<DatabaseContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")),
                ServiceLifetime.Transient,
                ServiceLifetime.Transient);

            // The command pipeline depends on the IDatabaseContext abstraction; map it onto the
            // same transient DatabaseContext registration.
            builder.Services.AddTransient<IDatabaseContext>(serviceProvider =>
                serviceProvider.GetRequiredService<DatabaseContext>());

            using var host = builder.Build();

            // The version line is what the deploy verification matches: it logs before
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
