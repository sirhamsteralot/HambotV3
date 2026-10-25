using Discord;
using HambotV2.Connections;
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
        private readonly ICommandService _commandService;
        private readonly IGuildSettingsService _guildSettingsService;
        private readonly IPermissionsService _permissionsService;
        private readonly ICooldownService _cooldownService;
        private readonly IDatabaseContext _databaseContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CommandHandler> _logger;

        public CommandHandler(
            ICommandService commandService,
            IGuildSettingsService guildSettingsService,
            IPermissionsService permissionsService,
            ICooldownService cooldownService,
            IDatabaseContext databaseContext,
            IConfiguration configuration,
            ILogger<CommandHandler> logger)
        {
            _commandService = commandService;
            _guildSettingsService = guildSettingsService;
            _permissionsService = permissionsService;
            _cooldownService = cooldownService;
            _databaseContext = databaseContext;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task HandleCommand(IMessage message)
        {
            ICommand? command = null;
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

                if (!_commandService.TryGetCommand(message.Content, identifier, out command))
                {
                    // Identifier-only messages (a bare "!") are ignored; only unknown command
                    // names are answered.
                    if (_commandService.IsIdentifierOnlyMessage(message.Content, identifier))
                        return;

                    await message.Channel.SendMessageAsync("The requested command is unavailable");
                    outcome = CommandOutcome.UnknownCommand;
                    return;
                }

                // The guild owner and the configured bot owner hold implicit tiers (owner /
                // bot owner) that need no database entry.
                var overrides = new TierOverrides
                {
                    GuildOwnerId = guildChannel?.Guild.OwnerId,
                    BotOwnerId = ParseConfiguredDiscordId(_configuration["Permissions:BotOwnerDiscordId"])
                };

                var permissionCheck = await _permissionsService.CheckUserPermissions(message.Author.Id, guildId, overrides, command, _databaseContext);
                if (!permissionCheck.IsAllowed)
                {
                    await message.Channel.SendMessageAsync("You are not permitted to use that command");
                    outcome = CommandOutcome.DeniedPermission;
                    return;
                }

                // Runs after the permission check and reuses the effective tier it already
                // computed, so the cooldown window cannot be bypassed via the tier lookups.
                var cooldownCheck = _cooldownService.TryBeginInvocation(guildId, message.Author.Id, command.Name, permissionCheck.UserEffectiveTier, _databaseContext);
                if (!cooldownCheck.IsAllowed)
                {
                    int remainingSeconds = Math.Max(1, (int)Math.Ceiling(cooldownCheck.RemainingWait.TotalSeconds));
                    string unit = remainingSeconds == 1 ? "second" : "seconds";
                    await message.Channel.SendMessageAsync($"You're using commands too quickly — please wait {remainingSeconds} more {unit}.");
                    outcome = CommandOutcome.Throttled;
                    return;
                }

                await command.ExecuteCommand(message, new CommandContext(_databaseContext, guildId, identifier, _guildSettingsService, overrides));
                outcome = CommandOutcome.Executed;
            }
            catch (Exception ex)
            {
                // Exception details go to the log only — never to Discord.
                outcome = CommandOutcome.Failed;
                _logger.LogError(ex, "Command '{commandName}' failed while processing message: {content}", command?.Name ?? "unknown", message.Content);

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
                // from it (only the failure path above logs the content).
                _logger.LogInformation(
                    "Command {CommandName} handled with outcome {Outcome} for user {UserId} in guild {GuildId}, channel {ChannelId} within {DurationMs} ms",
                    command?.Name, outcome, message.Author.Id, guildId, message.Channel.Id, stopwatch.Elapsed.TotalMilliseconds);
            }
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
