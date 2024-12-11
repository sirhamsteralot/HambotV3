using Discord;
using Discord.WebSocket;

namespace HambotV2
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;

        private DiscordSocketClient _client;
        private string _logFormat;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _logFormat = _configuration["BotConfiguration:LogFormat"]!;
            _client = new DiscordSocketClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Initializing Discord connection...");


            _client.Log += ClientLogged;

            var token = _configuration["Secrets:BotToken"];

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Worker running at: {time}, discord status: {1}", DateTimeOffset.Now, _client.ConnectionState.ToString());
                }
                await Task.Delay(10000, stoppingToken);
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
