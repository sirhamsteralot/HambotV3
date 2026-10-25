using Discord;
using HambotV2.Connections;
using HambotV2.Maintenance;
using HambotV2.Watchdog;
using System.Diagnostics;

namespace HambotV2.Commands
{
    /// <summary>
    /// Per-message command pipeline: resolve the command with the guild's identifier, check
    /// permissions, check the cooldown, execute. Any failure is contained to this message — it is
    /// logged and answered with a generic reply, and the pipeline stays usable for the next one.
    /// </summary>
    internal sealed class CommandHandler : ICommandHandler
    {
        /// <summary>The reply other commands get while a backup restore is in progress (FR-98).</summary>
        internal const string MaintenanceNotice = "The bot is in maintenance mode (a backup restore is in progress) — please try again in a moment.";

        private readonly ICommandService _commandService;
        private readonly IGuildSettingsService _guildSettingsService;
        private readonly IPermissionsService _permissionsService;
        private readonly ICooldownService _cooldownService;
        private readonly IDatabaseContext _databaseContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CommandHandler> _logger;
        private readonly IMaintenanceMode? _maintenanceMode;
        private readonly INotificationPublisher? _notificationPublisher;

        public CommandHandler(
            ICommandService commandService,
            IGuildSettingsService guildSettingsService,
            IPermissionsService permissionsService,
            ICooldownService cooldownService,
            IDatabaseContext databaseContext,
            IConfiguration configuration,
            ILogger<CommandHandler> logger,
            IMaintenanceMode? maintenanceMode = null,
            INotificationPublisher? notificationPublisher = null)
        {
            _commandService = commandService;
            _guildSettingsService = guildSettingsService;
            _permissionsService = permissionsService;
            _cooldownService = cooldownService;
            _databaseContext = databaseContext;
            _configuration = configuration;
            _logger = logger;
            _maintenanceMode = maintenanceMode;
            _notificationPublisher = notificationPublisher;
        }

        public async Task HandleCommand(IMessage message)
        {
            ICommand? command = null;

            // Set when a custom response was dispatched, so the audit event names it (FR-54).
            string? responseName = null;

            var outcome = CommandOutcome.Ignored;
            var stopwatch = Stopwatch.StartNew();
            ulong? guildId = null;

            try
            {
                // A null guild id marks the DM scope (FR-25): the user acts at owner tier, commands
                // require their built-in default tiers, and no per-guild state is read or written.
                var guildChannel = message.Channel as IGuildChannel;
                guildId = guildChannel?.GuildId;

                string identifier = guildId is ulong guild
                    ? _guildSettingsService.GetCommandIdentifier(guild)
                    : _guildSettingsService.GlobalCommandIdentifier;

                // The guild owner and the configured bot owner hold implicit tiers (owner /
                // bot owner) that need no database entry.
                var overrides = new TierOverrides
                {
                    GuildOwnerId = guildChannel?.Guild.OwnerId,
                    BotOwnerId = ParseConfiguredDiscordId(_configuration["Permissions:BotOwnerDiscordId"])
                };

                if (_commandService.TryGetCommand(message.Content, identifier, out command))
                {
                    // Maintenance mode (FR-98): while a backup restore replaces the database, every
                    // command declines except the restore command finishing its own confirmation.
                    if (_maintenanceMode?.IsActive == true && command.Name != BackupCommands.RestoreBackupCommand.CommandName)
                    {
                        await message.Channel.SendMessageAsync(MaintenanceNotice);
                        outcome = CommandOutcome.MaintenanceMode;
                        return;
                    }

                    outcome = await ExecuteBuiltInCommandAsync(message, guildId, identifier, overrides, command);
                    return;
                }

                // Identifier-only messages (a bare "!") are ignored; only unknown command
                // names are answered.
                if (_commandService.IsIdentifierOnlyMessage(message.Content, identifier))
                    return;

                // Custom-response fallback (FR-54): before the "unavailable" reply, a guild
                // message's command token is checked against the invoking guild's custom
                // responses. Built-ins always win — this branch only runs after the built-in
                // lookup failed (FR-53). Direct messages keep the plain "unavailable" reply
                // (FR-59).
                if (guildId is ulong guildIdValue
                    && CommandParsing.TryGetCommandName(message.Content, identifier, out string token))
                {
                    CommandOutcome? responseOutcome = await TryDispatchCustomResponseAsync(message, guildIdValue, overrides, token);
                    if (responseOutcome is not null)
                    {
                        outcome = responseOutcome.Value;
                        responseName = token;
                        return;
                    }
                }

                await message.Channel.SendMessageAsync("The requested command is unavailable");
                outcome = CommandOutcome.UnknownCommand;
            }
            catch (Exception ex)
            {
                // Exception details go to the log only — never to Discord.
                outcome = CommandOutcome.Failed;
                _logger.LogError(ex, "Command '{commandName}' failed while processing message: {content}", command?.Name ?? responseName ?? "unknown", message.Content);

                // The failure is also posted to the operator notification channel when one is
                // configured (FR-118); the publisher rate-limits and never throws back.
                if (_notificationPublisher is not null)
                {
                    await _notificationPublisher.PublishAsync(
                        $"Command execution failure: command '{command?.Name ?? responseName ?? "unknown"}' invoked by user {message.Author.Id} in guild {guildId?.ToString() ?? "DMs"} failed.");
                }

                try
                {
                    await message.Channel.SendMessageAsync("An unexpected error occurred while executing the command. Please try again later.");
                }
                catch (Exception replyEx)
                {
                    _logger.LogError(replyEx, "Failed to send error reply for message: {content}", message.Content);
                }
            }
            finally
            {
                // The single audit event per message; message content is deliberately absent
                // from it (only the failure path above logs the content). A dispatched custom
                // response logs its name as the CommandName (NFR-11).
                _logger.LogInformation(
                    "Command {CommandName} handled with outcome {Outcome} for user {UserId} in guild {GuildId}, channel {ChannelId} within {DurationMs} ms",
                    command?.Name ?? responseName, outcome, message.Author.Id, guildId, message.Channel.Id, stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>Executes a built-in command through the permission and cooldown gates; returns the outcome for the audit event.</summary>
        private async Task<CommandOutcome> ExecuteBuiltInCommandAsync(IMessage message, ulong? guildId, string identifier, TierOverrides overrides, ICommand command)
        {
            var permissionCheck = await _permissionsService.CheckUserPermissions(message.Author.Id, guildId, overrides, command, _databaseContext);
            if (!permissionCheck.IsAllowed)
            {
                await message.Channel.SendMessageAsync("You are not permitted to use that command");
                return CommandOutcome.DeniedPermission;
            }

            // Runs after the permission check and reuses the effective tier it already
            // computed, so the cooldown window cannot be bypassed via the tier lookups.
            var cooldownCheck = _cooldownService.TryBeginInvocation(guildId, message.Author.Id, command.Name, permissionCheck.UserEffectiveTier, _databaseContext);
            if (!cooldownCheck.IsAllowed)
            {
                await SendThrottleReplyAsync(message, cooldownCheck);
                return CommandOutcome.Throttled;
            }

            await command.ExecuteCommand(message, new CommandContext(_databaseContext, guildId, identifier, _guildSettingsService, overrides));
            return CommandOutcome.Executed;
        }

        /// <summary>
        /// Dispatches a custom response when the guild defines one under the given name (FR-54);
        /// returns null when it does not (the caller falls through to the "unavailable" reply).
        /// The invocation goes through the same checks as a built-in, in the same order (FR-55):
        /// the permission check — required tier from the (guild, name) CommandMetaData override,
        /// default 0 — then the cooldown, keyed on the response name so it throttles independently
        /// of any built-in. Denial and throttle replies are the built-in ones.
        /// </summary>
        private async Task<CommandOutcome?> TryDispatchCustomResponseAsync(IMessage message, ulong guildId, TierOverrides overrides, string responseName)
        {
            // Maintenance mode (FR-98): custom responses touch database state too.
            if (_maintenanceMode?.IsActive == true)
            {
                await message.Channel.SendMessageAsync(MaintenanceNotice);
                return CommandOutcome.MaintenanceMode;
            }

            var response = await _databaseContext.CustomResponses.FindAsync(guildId, responseName);
            if (response is null)
                return null;

            var permissionCheck = await _permissionsService.CheckUserPermissions(message.Author.Id, guildId, overrides, responseName, PermissionTier.Everyone, _databaseContext);
            if (!permissionCheck.IsAllowed)
            {
                await message.Channel.SendMessageAsync("You are not permitted to use that command");
                return CommandOutcome.DeniedPermission;
            }

            var cooldownCheck = _cooldownService.TryBeginInvocation(guildId, message.Author.Id, responseName, permissionCheck.UserEffectiveTier, _databaseContext);
            if (!cooldownCheck.IsAllowed)
            {
                await SendThrottleReplyAsync(message, cooldownCheck);
                return CommandOutcome.Throttled;
            }

            // Plain text only (FR-60): the stored content is sent verbatim; the bot uploads no files.
            await message.Channel.SendMessageAsync(response.Content);
            return CommandOutcome.Executed;
        }

        /// <summary>The shared throttle reply for built-in and custom-response dispatch.</summary>
        private static async Task SendThrottleReplyAsync(IMessage message, CooldownCheck cooldownCheck)
        {
            int remainingSeconds = Math.Max(1, (int)Math.Ceiling(cooldownCheck.RemainingWait.TotalSeconds));
            string unit = remainingSeconds == 1 ? "second" : "seconds";
            await message.Channel.SendMessageAsync($"You're using commands too quickly — please wait {remainingSeconds} more {unit}.");
        }

        public void Dispose()
        {
            // The handler is created per message; its transient DatabaseContext would otherwise be
            // disposed only when the root provider shuts down, leaking connections across messages.
            _databaseContext.Dispose();
        }

        private static ulong? ParseConfiguredDiscordId(string? configuredId)
        {
            return ulong.TryParse(configuredId, out ulong parsedId) ? parsedId : null;
        }
    }
}
