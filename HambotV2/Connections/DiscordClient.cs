using Discord;
using Discord.WebSocket;
using HambotV2.Commands;

namespace HambotV2.Connections
{
    internal class DiscordClient : BackgroundService
    {
        private readonly ILogger<DiscordClient> _logger;
        private readonly IConfiguration _configuration;
        private readonly IGuildSettingsService _guildSettingsService;

        private DiscordSocketClient _client;
        private string _logFormat;
        private IServiceProvider _serviceProvider;

        public DiscordClient(ILogger<DiscordClient> logger, IConfiguration configuration, IServiceProvider serviceProvider, IGuildSettingsService guildSettingsService)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
            _guildSettingsService = guildSettingsService;
            _logFormat = _configuration["BotConfiguration:LogFormat"]!;
            // Only request the intents the bot actually uses; AllUnprivileged would make Discord.Net
            // warn about unused intents (e.g. GuildScheduledEvents, GuildInvites) at every connect.
            _client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Initializing Discord connection...");


            _client.Log += ClientLogged;
            _client.Disconnected += ClientDisconnected;
            _client.Connected += ClientConnected;

            var token = _configuration["Secrets:BotToken"];

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            _client.MessageReceived += _client_MessageReceived;

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Worker running at: {time}, discord status: {1}", DateTimeOffset.Now, _client.ConnectionState.ToString());
                    }
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the host requests shutdown; cleanup happens in the finally block.
            }
            finally
            {
                await ShutdownClientAsync();
            }
        }

        /// <summary>
        /// Stops the gateway, logs the Discord client out and releases its resources.
        /// Runs on every exit path of <see cref="ExecuteAsync"/>, including startup failures,
        /// so the bot never dies mid-connection without a clean logout.
        /// </summary>
        private async Task ShutdownClientAsync()
        {
            _logger.LogInformation("Shutting down Discord connection...");
            _client.MessageReceived -= _client_MessageReceived;
            _client.Disconnected -= ClientDisconnected;
            _client.Connected -= ClientConnected;

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
                _client.Dispose();
                _logger.LogInformation("Discord connection shut down cleanly.");
            }
        }

        private Task ClientDisconnected(Exception exception)
        {
            _logger.LogWarning("Discord gateway disconnected: {reason}. Discord.Net will attempt to reconnect automatically.", exception.Message);
            return Task.CompletedTask;
        }

        private Task ClientConnected()
        {
            _logger.LogInformation("Discord gateway connection established.");
            return Task.CompletedTask;
        }

        private async Task _client_MessageReceived(SocketMessage arg)
        {
            // The gateway delivers the bot's own messages (and other bots'/webhooks') too. Without
            // this guard the bot would re-process its own replies that start with the command
            // identifier — e.g. the !help detail output — and answer itself.
            if (arg.Author.IsBot || arg.Author.IsWebhook)
                return;

            // Per-guild identifier (roadmap T14, FR-26): each guild's effective identifier decides
            // which messages are command invocations; direct messages use the global default, since
            // DM commands run in the DM scope (see CommandHandler).
            string identifier = arg.Channel is SocketGuildChannel guildChannel
                ? _guildSettingsService.GetCommandIdentifier(guildChannel.Guild.Id)
                : _guildSettingsService.GlobalCommandIdentifier;

            if (arg.Content.StartsWith(identifier))
            {
                using (var commandHandler = _serviceProvider.GetService<ICommandHandler>())
                {
                    if (commandHandler != null)
                    {
                        await commandHandler.HandleCommand(arg);
                    } else
                    {
                        _logger.LogWarning("No Command Handler found yet command executed!");
                    }
                }
            }
        }

        private Task ClientLogged(LogMessage logMessage)
        {
            switch (logMessage.Severity)
            {
                case LogSeverity.Verbose:
                    _logger.Log(LogLevel.Trace, _logFormat, logMessage.Source, logMessage.Message);
                    break;
                case LogSeverity.Debug:
                    _logger.Log(LogLevel.Debug, _logFormat, logMessage.Source, logMessage.Message);
                    break;
                case LogSeverity.Info:
                    _logger.Log(LogLevel.Information, _logFormat, logMessage.Source, logMessage.Message);
                    break;
                case LogSeverity.Warning:
                    _logger.Log(LogLevel.Warning, _logFormat, logMessage.Source, logMessage.Message);
                    break;
                case LogSeverity.Error:
                    _logger.Log(LogLevel.Error, _logFormat, logMessage.Source, logMessage.Message);
                    break;
                case LogSeverity.Critical:
                    _logger.Log(LogLevel.Critical, _logFormat, logMessage.Source, logMessage.Message);
                    break;
            }

            return Task.CompletedTask;
        }
    }
}
