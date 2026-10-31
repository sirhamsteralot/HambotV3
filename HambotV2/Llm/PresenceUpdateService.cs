using Discord;

using HambotV2.Connections;

namespace HambotV2.Llm
{
    /// <summary>
    /// The opt-in LLM loop: on each interval tick it builds the prompt (persona resolved per the
    /// chain in <see cref="LlmPersona"/>, optional bounded chat context), calls the model through
    /// the gate, validates the output per surface, and applies it via the presence service —
    /// presence and, when enabled, the bio in the same tick. Any client error or invalid output
    /// keeps the current state, is logged, and retries next tick; nothing ever crashes the bot.
    /// Without an API key (or with a zero budget) the service stays inactive.
    /// </summary>
    internal sealed class PresenceUpdateService : BackgroundService
    {
        private readonly LlmSettings _settings;
        private readonly ILlmGate _gate;
        private readonly LlmPromptBuilder _promptBuilder;
        private readonly IChatContextCollector? _chatContext;
        private readonly IPresenceService _presence;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly string _botName;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<PresenceUpdateService> _logger;

        public PresenceUpdateService(
            LlmSettings settings,
            ILlmGate gate,
            LlmPromptBuilder promptBuilder,
            IPresenceService presence,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            TimeProvider timeProvider,
            ILogger<PresenceUpdateService> logger,
            IChatContextCollector? chatContext = null)
        {
            _settings = settings;
            _gate = gate;
            _promptBuilder = promptBuilder;
            _presence = presence;
            _scopeFactory = scopeFactory;
            _botName = configuration["BotConfiguration:BotName"] ?? "HamBot";
            _timeProvider = timeProvider;
            _logger = logger;
            _chatContext = chatContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Active)
            {
                _logger.LogInformation("LLM features are inactive (no Llm:ApiKey, or the daily budget is 0).");
                return;
            }

            if (!_settings.PresenceUpdatesEnabled && !_settings.BioUpdatesEnabled)
            {
                _logger.LogInformation("LLM features are active but no update job is enabled.");
                return;
            }

            _logger.LogInformation("Presence/bio update service started (interval {Interval} minutes).", _settings.PresenceUpdatesIntervalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Presence/bio update tick failed; retrying at the next interval.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_settings.PresenceUpdatesIntervalMinutes), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Presence/bio update service stopped.");
        }

        /// <summary>One update pass; exposed so tests drive the cadence directly.</summary>
        internal async Task RunTickAsync(CancellationToken cancellationToken = default)
        {
            string persona = ResolveGlobalPersona();
            string? chat = _settings.IncludeChat
                ? await (_chatContext?.CollectAsync(_settings.ChatContextMaxMessages, _settings.ChatContextMaxCharacters, cancellationToken) ?? Task.FromResult<string?>(null))
                : null;

            string currentActivity = _presence.CurrentActivity is { } activity
                ? $"{PresenceActivityTypes.GetLabel(activity.Type)} {activity.Text}"
                : "none";

            if (_settings.PresenceUpdatesEnabled)
            {
                var input = new LlmPromptInput(persona, _botName, BotVersion.Current, _timeProvider.GetUtcNow(), currentActivity, chat, LlmSurface.Presence);
                LlmResponse? response = await _gate.ChatAsync("presence", _promptBuilder.Build(input), cancellationToken);
                if (response is null)
                    return;

                if (!LlmOutputValidator.TryValidatePresence(response.Content, out string text))
                {
                    _logger.LogWarning("LLM returned an invalid presence (empty, multi-line, or over {Max} chars); keeping the current one.", ProfileLimits.MaxActivityTextLength);
                    _gate.RecordInvalidOutput();
                    return;
                }

                ActivityType type = _presence.CurrentActivity?.Type ?? ActivityType.Playing;
                await _presence.ApplyActivityAsync(type, text);
            }

            if (_settings.BioUpdatesEnabled)
            {
                var input = new LlmPromptInput(persona, _botName, BotVersion.Current, _timeProvider.GetUtcNow(), currentActivity, chat, LlmSurface.Bio);
                LlmResponse? response = await _gate.ChatAsync("bio", _promptBuilder.Build(input), cancellationToken);
                if (response is null)
                    return;

                if (!LlmOutputValidator.TryValidateBio(response.Content, out string bio))
                {
                    _logger.LogWarning("LLM returned an invalid bio (empty or over {Max} chars); keeping the current one.", ProfileLimits.MaxBioLength);
                    _gate.RecordInvalidOutput();
                    return;
                }

                await _presence.ApplyBioAsync(bio);
            }
        }

        private string ResolveGlobalPersona()
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
            return LlmPersona.ResolveGlobalWithLayer(database, _settings).Persona;
        }
    }
}
