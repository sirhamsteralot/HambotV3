using Discord;
using HambotV2.Connections;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal class CommandHandler : ICommandHandler
    {
        private ILogger<CommandHandler> _logger;
        private IConfiguration _configuration;
        private ICommandService _commandService;
        private IDatabaseContext _databaseContext;
        private IPermissionsService _permissionsService;
        private IGuildSettingsService _guildSettingsService;

        public CommandHandler(ICommandService commandService, ILogger<CommandHandler> logger, IConfiguration configuration, IDatabaseContext databaseContext, IPermissionsService permissionsService, IGuildSettingsService guildSettingsService)
        {
            _logger = logger;
            _configuration = configuration;
            _commandService = commandService;
            _databaseContext = databaseContext;
            _permissionsService = permissionsService;
            _guildSettingsService = guildSettingsService;
        }

        public async Task HandleCommand(IMessage socketMessageCommand)
        {
            ICommand? command = null;
            var outcome = CommandOutcome.Ignored;
            var stopwatch = Stopwatch.StartNew();
            ulong? guildId = null;
            try
            {
                // A null guild id means the message is a direct message (roadmap T14, FR-25). DMs use
                // the global identifier and the DM permission scope: the user acts at owner tier and
                // commands require their built-in defaults — no per-guild state is read or written,
                // so DMs need no database rows. Commands that manage guild state refuse themselves.
                var guildChannel = socketMessageCommand.Channel as IGuildChannel;
                guildId = guildChannel?.GuildId;

                string identifier = guildId is ulong guild
                    ? _guildSettingsService.GetCommandIdentifier(guild)
                    : _guildSettingsService.GlobalCommandIdentifier;

                if (!_commandService.TryGetCommand(socketMessageCommand.Content, identifier, out command)) {
                    // Identifier-only messages (e.g. a bare "!", roadmap T5) are ignored silently
                    // instead of being answered like unknown commands.
                    if (_commandService.IsIdentifierOnlyMessage(socketMessageCommand.Content, identifier))
                        return;

                    await socketMessageCommand.Channel.SendMessageAsync("The requested command is unavailable");
                    outcome = CommandOutcome.UnknownCommand;
                    return;
                }

                // Tier overrides (roadmap T16): the guild owner acts at owner tier in their guild
                // even without any database entry; the configured bot owner acts at the globally
                // highest tier in every guild.
                var overrides = new TierOverrides
                {
                    GuildOwnerId = guildChannel?.Guild.OwnerId,
                    BotOwnerId = ParseConfiguredDiscordId(_configuration["Permissions:BotOwnerDiscordId"])
                };

                if (!await _permissionsService.CheckUserPermissions(socketMessageCommand.Author.Id, guildId, overrides, command, _databaseContext))
                {
                    await socketMessageCommand.Channel.SendMessageAsync("You are not permitted to use that command");
                    outcome = CommandOutcome.DeniedPermission;
                    return;
                }

                await command.ExecuteCommand(socketMessageCommand, new CommandContext(_databaseContext, guildId, identifier, _guildSettingsService, overrides));
                outcome = CommandOutcome.Executed;
            }
            catch (Exception ex)
            {
                // A single failing message (throwing command, database outage, ...) must never take the
                // bot down: every failure is handled independently and the pipeline stays usable for
                // subsequent messages. Exception details are logged but never sent to Discord.
                outcome = CommandOutcome.Failed;
                _logger.LogError(ex, "Command '{commandName}' failed while processing message: {content}", command?.Name ?? "unknown", socketMessageCommand.Content);

                try
                {
                    await socketMessageCommand.Channel.SendMessageAsync("An unexpected error occurred while executing the command. Please try again later.");
                }
                catch (Exception replyEx)
                {
                    _logger.LogError(replyEx, "Failed to send error reply for message: {content}", socketMessageCommand.Content);
                }
            }
            finally
            {
                // One structured audit event per handled message (roadmap T12, NFR-11): command usage
                // is traceable from logs without debugging. Named placeholders keep the fields
                // filterable; message content is deliberately absent (only the failure path logs it).
                _logger.LogInformation(
                    "Command {CommandName} handled with outcome {Outcome} for user {UserId} in guild {GuildId}, channel {ChannelId} within {DurationMs} ms",
                    command?.Name, outcome, socketMessageCommand.Author.Id, guildId, socketMessageCommand.Channel.Id, stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        private static ulong? ParseConfiguredDiscordId(string? configuredId)
        {
            return ulong.TryParse(configuredId, out ulong parsedId) ? parsedId : null;
        }

        public void Dispose()
        {
            // The handler is created per message; its DatabaseContext is transient and would otherwise
            // only be disposed when the root provider shuts down, leaking connections across messages.
            // Nulling the fields on a disposed object requires the null-forgiving operator.
            _databaseContext?.Dispose();
            _logger = null!;
            _configuration = null!;
            _databaseContext = null!;
            _commandService = null!;
            _guildSettingsService = null!;
        }
    }
}
