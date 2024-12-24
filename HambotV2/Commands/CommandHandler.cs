using Discord.WebSocket;
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

        public async Task HandleCommand(SocketMessage socketMessageCommand)
        {
            if (!_commandService.TryGetCommand(socketMessageCommand.Content, out ICommand command)) {
                _logger.LogInformation("Unavailable command executed! from message: {content}", socketMessageCommand.Content);
                socketMessageCommand.Channel.SendMessageAsync("The requested command is unavailable");
                return;
            }

            if (!await _permissionsService.CheckUserPermissions(socketMessageCommand.Author.Id, command, _databaseContext))
            {
                _logger.LogInformation("No sufficient permissions to execute command! from message: {content}", socketMessageCommand.Content);
                socketMessageCommand.Channel.SendMessageAsync("You are not permitted to use that command");
                return;
            }

            _logger.LogInformation("Executed command: {command}", socketMessageCommand.Content);
            await command.ExecuteCommand(socketMessageCommand, new CommandContext(_databaseContext));
        }

        public void Dispose()
        {
            _logger = null;
            _configuration = null;
            _databaseContext = null;
            _commandService = null;
        }
    }
}
