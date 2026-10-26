using Discord;
using Discord.WebSocket;
using HambotV2.Commands;

namespace HambotV2.Connections
{
    internal class DiscordClient : BackgroundService
    {
        private readonly ILogger<DiscordClient> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly IGuildSettingsService _guildSettingsService;
        private readonly string _logFormat;
        private readonly DiscordSocketClient _client;

        public DiscordClient(ILogger<DiscordClient> logger, IConfiguration configuration, IServiceProvider serviceProvider, IGuildSettingsService guildSettingsService, DiscordSocketClient client)
        {
            _logger = logger;
            _configuration = configuration;
            _serviceProvider = serviceProvider;
            _guildSettingsService = guildSettingsService;
            _logFormat = configuration["BotConfiguration:LogFormat"]!;
            // The client comes from DI (shared with ReminderDmSender, container-owned);
            // its gateway intents are configured once at the composition root.
            _client = client;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Initializing Discord connection...");

            _client.Log += OnClientLog;
            _client.Connected += OnConnected;
            _client.Disconnected += OnDisconnected;
            _client.MessageReceived += OnMessageReceived;

            await _client.LoginAsync(TokenType.Bot, _configuration["Secrets:BotToken"]);
            await _client.StartAsync();

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Worker running at: {Time}, discord status: {Status}", DateTimeOffset.Now, _client.ConnectionState);

                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the host requests shutdown; cleanup happens in the finally block.
            }
            finally
            {
                await ShutdownAsync();
            }
        }

        /// <summary>
        /// Stops the gateway and logs the Discord client out. Runs on every exit path of
        /// <see cref="ExecuteAsync"/>, so the bot never dies mid-connection without a clean
        /// logout.
        /// </summary>
        private async Task ShutdownAsync()
        {
            _logger.LogInformation("Shutting down Discord connection...");
            _client.MessageReceived -= OnMessageReceived;
            _client.Disconnected -= OnDisconnected;
            _client.Connected -= OnConnected;
            _client.Log -= OnClientLog;

            try
            {
                await _client.StopAsync();
                await _client.LogoutAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while shutting down the Discord connection.");
            }
            finally
            {
                // The DI container owns and disposes the shared client after the hosted services
                // stop; this only logs out and stops the gateway.
                _logger.LogInformation("Discord connection shut down cleanly.");
            }
        }

        private async Task OnMessageReceived(SocketMessage message)
        {
            // The gateway delivers the bot's own messages (and other bots'/webhooks') too. Without
            // this guard the bot would re-process its own replies that start with the command
            // identifier — e.g. the !help detail output — and answer itself.
            if (message.Author.IsBot || message.Author.IsWebhook)
                return;

            // Each guild's effective identifier decides which messages are command invocations
            //; direct messages use the global default (DM commands run in the DM scope,
            // see CommandHandler).
            string identifier = message.Channel is SocketGuildChannel guildChannel
                ? _guildSettingsService.GetCommandIdentifier(guildChannel.Guild.Id)
                : _guildSettingsService.GlobalCommandIdentifier;

            if (!message.Content.StartsWith(identifier))
                return;

            using var handler = _serviceProvider.GetService<ICommandHandler>();
            if (handler is null)
            {
                _logger.LogWarning("No Command Handler found yet command executed!");
                return;
            }

            await handler.HandleCommand(message);
        }

        private Task OnConnected()
        {
            _logger.LogInformation("Discord gateway connection established.");
            return Task.CompletedTask;
        }

        private Task OnDisconnected(Exception exception)
        {
            _logger.LogWarning("Discord gateway disconnected: {reason}. Discord.Net will attempt to reconnect automatically.", exception.Message);
            return Task.CompletedTask;
        }

        private Task OnClientLog(LogMessage logMessage)
        {
            _logger.Log(ToLogLevel(logMessage.Severity), _logFormat, logMessage.Source, logMessage.Message);
            return Task.CompletedTask;
        }

        private static LogLevel ToLogLevel(LogSeverity severity) => severity switch
        {
            LogSeverity.Verbose => LogLevel.Trace,
            LogSeverity.Debug => LogLevel.Debug,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Critical => LogLevel.Critical,
            _ => LogLevel.Information
        };
    }
}
