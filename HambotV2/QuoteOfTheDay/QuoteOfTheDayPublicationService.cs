using HambotV2.Commands.QuoteCommands;
using HambotV2.Connections;
using HambotV2.Maintenance;
using HambotV2.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.QuoteOfTheDay
{
    /// <summary>
    /// The hosted quote-of-the-day publication loop: polls the configured guilds on a short
    /// fixed cadence and, at each guild's schedule boundary, publishes one random quote — the
    /// same random-fetch semantics as <c>!quote</c> — rendered through the guild's template into
    /// its target channel. The schedule is fixed wall-clock state derived from the configuration,
    /// never from the last post: boundaries are the anchor time plus whole multiples of the
    /// interval, all in UTC, and the per-guild boundary memory lives in process
    /// state only, so a restart re-baselines every guild and boundaries that passed while the
    /// bot was down are skipped — one publication at the next boundary, never a catch-up burst.
    /// A failed post consumes its boundary and leaves the schedule unchanged; maintenance mode
    /// pauses the loop on the shared periodic-job host (§3.8) like reminder deliveries, and
    /// boundaries that pass during it are skipped the same way.
    /// </summary>
    internal sealed class QuoteOfTheDayPublicationService : BackgroundService
    {
        /// <summary>Poll cadence: bounds how late a publication can run after its boundary.</summary>
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IQuoteOfTheDayChannelSender _sender;
        private readonly TimeProvider _timeProvider;
        private readonly IMaintenanceMode? _maintenanceMode;
        private readonly ILogger<QuoteOfTheDayPublicationService> _logger;

        /// <summary>
        /// The boundary each guild last published (or baselined) under its current schedule: the
        /// key to the skip semantics — a boundary publishes only when the loop observes it pass.
        /// </summary>
        private readonly Dictionary<ulong, GuildBoundary> _lastBoundary = new();

        public QuoteOfTheDayPublicationService(
            IServiceScopeFactory scopeFactory,
            IQuoteOfTheDayChannelSender sender,
            TimeProvider timeProvider,
            ILogger<QuoteOfTheDayPublicationService> logger,
            IMaintenanceMode? maintenanceMode = null)
        {
            _scopeFactory = scopeFactory;
            _sender = sender;
            _timeProvider = timeProvider;
            _logger = logger;
            _maintenanceMode = maintenanceMode;
        }

        private sealed record GuildBoundary(QuoteOfTheDaySchedule Schedule, DateTimeOffset Boundary);

        /// <summary>
        /// The publication loop lives on the shared periodic-job host (§3.8) at the poll cadence.
        /// Maintenance mode pauses the run; when it ends, the boundary memory is dropped so the
        /// first pass re-baselines every guild — boundaries that passed during the restore are
        /// skipped, never caught up. Exposed so tests drive the loop over FakeTimeProvider.
        /// </summary>
        internal PeriodicJobHost CreateJobHost() => new(
            "quote-of-the-day",
            () => PollInterval,
            RunPassAsync,
            _timeProvider,
            _logger,
            _maintenanceMode,
            onMaintenanceResumed: () =>
            {
                _lastBoundary.Clear();
                return Task.CompletedTask;
            });

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => CreateJobHost().RunAsync(stoppingToken);

        /// <summary>
        /// One publication pass at <paramref name="now"/>: baselines or publishes per configured
        /// guild. A guild seen for the first time (startup, restart, or a configuration change)
        /// baselines to the current boundary without publishing, so the first publication is at
        /// the next boundary; a boundary already recorded has been consumed and publishes
        /// nothing; a new boundary publishes once.
        /// </summary>
        internal async Task RunPassAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            List<DatabaseContext.GuildSetting> configuredGuilds = await database.GuildSettings
                .Where(setting => setting.QuoteOfTheDayChannelId != null)
                .ToListAsync(cancellationToken);

            foreach (var setting in configuredGuilds)
            {
                try
                {
                    var schedule = QuoteOfTheDaySchedule.FromSetting(setting);
                    DateTimeOffset boundary = schedule.CurrentBoundary(now);

                    if (!_lastBoundary.TryGetValue(setting.GuildId, out GuildBoundary? remembered) || remembered.Schedule != schedule)
                    {
                        _lastBoundary[setting.GuildId] = new GuildBoundary(schedule, boundary);
                        continue;
                    }

                    if (boundary <= remembered.Boundary)
                        continue;

                    // The boundary is consumed before the attempt: a failed post skips it and
                    // never shifts the schedule — the next publication is at the next boundary.
                    _lastBoundary[setting.GuildId] = new GuildBoundary(schedule, boundary);
                    await PublishAsync(database, setting, schedule, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Quote of the day publication for guild {GuildId} failed; the boundary is skipped.",
                        setting.GuildId);
                }
            }

            // Guilds that turned the feature off leave the memory, so a later re-enable
            // baselines fresh instead of resuming an old schedule's boundary.
            var activeGuildIds = configuredGuilds.Select(setting => setting.GuildId).ToHashSet();
            foreach (ulong guildId in _lastBoundary.Keys.Where(guildId => !activeGuildIds.Contains(guildId)).ToList())
                _lastBoundary.Remove(guildId);
        }

        private async Task PublishAsync(IDatabaseContext database, DatabaseContext.GuildSetting setting, QuoteOfTheDaySchedule schedule, CancellationToken cancellationToken)
        {
            var quote = await QuoteCommand.GetRandomQuoteAsync(database, setting.GuildId);
            if (quote is null)
            {
                _logger.LogInformation("Quote of the day boundary skipped: guild {GuildId} has no quotes.", setting.GuildId);
                return;
            }

            string message = QuoteOfTheDaySchedule.Render(QuoteOfTheDaySchedule.TemplateOf(setting), quote);
            await _sender.SendAsync(schedule.ChannelId, message, cancellationToken);

            _logger.LogInformation("Published the quote of the day to channel {ChannelId} in guild {GuildId}.",
                schedule.ChannelId, setting.GuildId);
        }
    }
}
