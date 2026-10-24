using Discord;
using HambotV2.Connections;
using System;
using System.Collections.Generic;
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
        private DatabaseContext _databaseContext;
        private IPermissionsService _permissionsService;

        public CommandHandler(ICommandService commandService, ILogger<CommandHandler> logger, IConfiguration configuration, DatabaseContext databaseContext, IPermissionsService permissionsService) 
        { 
            _logger = logger;
            _configuration = configuration;
            _commandService = commandService;
            _databaseContext = databaseContext;
            _permissionsService = permissionsService;
        }

        public async Task HandleCommand(IMessage socketMessageCommand)
        {
            ICommand command = null;
            try
            {
                if (!_commandService.TryGetCommand(socketMessageCommand.Content, out command)) {
                    _logger.LogInformation("Unavailable command executed! from message: {content}", socketMessageCommand.Content);
                    await socketMessageCommand.Channel.SendMessageAsync("The requested command is unavailable");
                    return;
                }

                if (!await _permissionsService.CheckUserPermissions(socketMessageCommand.Author.Id, command, _databaseContext))
                {
                    _logger.LogInformation("No sufficient permissions to execute command! from message: {content}", socketMessageCommand.Content);
                    await socketMessageCommand.Channel.SendMessageAsync("You are not permitted to use that command");
                    return;
                }

                await command.ExecuteCommand(socketMessageCommand, new CommandContext(_databaseContext));
            }
            catch (Exception ex)
            {
                // A single failing message (throwing command, database outage, ...) must never take the
                // bot down: every failure is handled independently and the pipeline stays usable for
                // subsequent messages. Exception details are logged but never sent to Discord.
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
        }

        public void Dispose()
        {
            // The handler is created per message; its DatabaseContext is transient and would otherwise
            // only be disposed when the root provider shuts down, leaking connections across messages.
            _databaseContext?.Dispose();
            _logger = null;
            _configuration = null;
            _databaseContext = null;
            _commandService = null;
        }
    }
}
