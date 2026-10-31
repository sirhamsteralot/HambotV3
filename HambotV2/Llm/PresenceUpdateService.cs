using Discord;

using HambotV2.Connections;

namespace HambotV2.Llm
{
    /// <summary>
    /// The opt-in LLM loop: on each job's interval tick it builds the prompt (persona resolved per the
    /// chain in <see cref="LlmPersona"/>, chat context from every guild that opted in via
    /// <c>!settings llmchat on</c>), calls the model through
    /// the gate, validates the output per surface, and applies it via the presence service. Presence
    /// and bio run on their own cadences (<c>Llm:PresenceUpdates:IntervalMinutes</c> /
    /// <c>Llm:BioUpdates:IntervalMinutes</c>); <see cref="UpdateAsync"/> triggers a surface manually
    /// regardless of the job flags. Any client error or invalid output keeps the current state, is
    /// logged, and retries next tick; nothing ever crashes the bot. Without an API key (or with a
    /// zero budget) the service stays inactive, but while active with no job enabled it stays alive
    /// so manual updates still work.
    /// </summary>
    internal sealed class PresenceUpdateService : BackgroundService, ILlmUpdateService
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

        /// <summary>Serializes scheduled ticks and manual updates so they never overlap.</summary>
        private readonly SemaphoreSlim _runLock = new(1, 1);

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
                _logger.LogInformation("LLM features are active but no update job is enabled (manual !llm update still works).");
            }
            else
            {
                if (_settings.PresenceUpdatesEnabled)
                    _logger.LogInformation("Presence update job started (interval {Interval} minutes).", _settings.PresenceUpdatesIntervalMinutes);
                if (_settings.BioUpdatesEnabled)
                    _logger.LogInformation("Bio update job started (interval {Interval} minutes).", _settings.BioUpdatesIntervalMinutes);
            }

            var loops = new List<Task>();
            if (_settings.PresenceUpdatesEnabled)
                loops.Add(RunScheduledLoopAsync(LlmSurface.Presence, _settings.PresenceUpdatesIntervalMinutes, stoppingToken));
            if (_settings.BioUpdatesEnabled)
                loops.Add(RunScheduledLoopAsync(LlmSurface.Bio, _settings.BioUpdatesIntervalMinutes, stoppingToken));

            if (loops.Count > 0)
                await Task.WhenAll(loops);
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

            _logger.LogInformation("Presence/bio update service stopped.");
        }

        private async Task RunScheduledLoopAsync(LlmSurface surface, int intervalMinutes, CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Automatic runs are global: they include chat from every opted-in guild.
                    await RunAutomaticAsync(surface, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Surface} update tick failed; retrying at the next interval.", surface);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>One update pass over the enabled surfaces; exposed so tests drive the cadence directly.</summary>
        internal async Task RunTickAsync(CancellationToken cancellationToken = default)
        {
            await _runLock.WaitAsync(cancellationToken);
            try
            {
                if (_settings.PresenceUpdatesEnabled)
                    await UpdateSurfaceAsync(LlmSurface.Presence, guildId: null, automatic: true, cancellationToken);
                if (_settings.BioUpdatesEnabled)
                    await UpdateSurfaceAsync(LlmSurface.Bio, guildId: null, automatic: true, cancellationToken);
            }
            finally
            {
                _runLock.Release();
            }
        }

        public async Task<LlmUpdateOutcome> UpdateAsync(LlmSurface surface, ulong? guildId = null, CancellationToken cancellationToken = default)
        {
            await _runLock.WaitAsync(cancellationToken);
            try
            {
                // Manual trigger: a guild run reads that guild's chat when opted in; a DM run (no
                // guild) reads no chat.
                return await UpdateSurfaceAsync(surface, guildId, automatic: false, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The manual !llm update trigger must never crash the command pipeline; an
                // unexpected failure (chat context, persona read, ...) is reported as Failed.
                // The scheduled loop retries at the next interval either way.
                _logger.LogError(ex, "{Surface} update failed; keeping the current state.", surface);
                return LlmUpdateOutcome.Failed;
            }
            finally
            {
                _runLock.Release();
            }
        }

        /// <summary>The scheduled-job entry point: a global run reading chat from every opted-in guild.</summary>
        private async Task<LlmUpdateOutcome> RunAutomaticAsync(LlmSurface surface, CancellationToken cancellationToken)
        {
            await _runLock.WaitAsync(cancellationToken);
            try
            {
                return await UpdateSurfaceAsync(surface, guildId: null, automatic: true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Surface} update failed; keeping the current state.", surface);
                return LlmUpdateOutcome.Failed;
            }
            finally
            {
                _runLock.Release();
            }
        }

        private async Task<LlmUpdateOutcome> UpdateSurfaceAsync(LlmSurface surface, ulong? guildId, bool automatic, CancellationToken cancellationToken)
        {
            if (!_gate.IsConfigured)
                return LlmUpdateOutcome.Inactive;

            var (persona, chatGuilds) = ResolveRunScope(guildId, automatic);
            // Chat context is per-guild opt-in: a guild-triggered run reads only that guild (when
            // opted in), an automatic run reads every opted-in guild, a DM run reads none.
            string? chat = chatGuilds.Count > 0
                ? await (_chatContext?.CollectAsync(chatGuilds, _settings.ChatContextMaxMessages, _settings.ChatContextMaxCharacters, cancellationToken) ?? Task.FromResult<string?>(null))
                : null;

            string currentActivity = _presence.CurrentActivity is { } activity
                ? $"{PresenceActivityTypes.GetLabel(activity.Type)} {activity.Text}"
                : "none";

            var input = new LlmPromptInput(persona, _botName, BotVersion.Current, _timeProvider.GetUtcNow(), currentActivity, chat, surface);
            string feature = surface == LlmSurface.Presence ? "presence" : "bio";
            LlmResponse? response = await _gate.ChatAsync(feature, _promptBuilder.Build(input), cancellationToken);
            if (response is null)
                return _gate.KillSwitchActive || _gate.BudgetExhausted ? LlmUpdateOutcome.Blocked : LlmUpdateOutcome.Failed;

            string text;
            bool valid = surface == LlmSurface.Presence
                ? LlmOutputValidator.TryValidatePresence(response.Content, out text)
                : LlmOutputValidator.TryValidateBio(response.Content, out text);
            if (!valid)
            {
                _logger.LogWarning("LLM returned an invalid {Surface} output; keeping the current one.", surface);
                _gate.RecordInvalidOutput();
                return LlmUpdateOutcome.InvalidOutput;
            }

            if (surface == LlmSurface.Presence)
            {
                ActivityType type = _presence.CurrentActivity?.Type ?? ActivityType.Playing;
                await _presence.ApplyActivityAsync(type, text);
            }
            else
            {
                await _presence.ApplyBioAsync(text);
            }

            return LlmUpdateOutcome.Applied;
        }

        /// <summary>Resolves the global persona and which guilds' chat this run may read: the invoking
        /// guild alone (when opted in) for a guild-triggered manual run, every opted-in guild for an
        /// automatic run, none for a DM-triggered manual run.</summary>
        private (string Persona, IReadOnlyList<ulong> ChatGuilds) ResolveRunScope(ulong? guildId, bool automatic)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
            string persona = LlmPersona.ResolveGlobalWithLayer(database, _settings).Persona;
            List<ulong> optedIn = database.GuildSettings
                .Where(setting => setting.LlmIncludeChat)
                .Select(setting => setting.GuildId)
                .ToList();
            IReadOnlyList<ulong> chatGuilds = guildId is { } guild
                ? optedIn.Contains(guild) ? [guild] : []
                : automatic ? optedIn : [];
            return (persona, chatGuilds);
        }
    }
}
