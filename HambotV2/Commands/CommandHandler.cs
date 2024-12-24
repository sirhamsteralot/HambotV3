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

        public CommandHandler(ICommandService commandService, ILogger<CommandHandler> logger, IConfiguration configuration, DatabaseContext databaseContext) 
        { 
            _logger = logger;
            _configuration = configuration;
            _commandService = commandService;
            _databaseContext = databaseContext;
        }

        public Task HandleCommand(SocketMessage socketMessageCommand)
        {
            if (!_commandService.TryGetCommand(socketMessageCommand.Content, out ICommand command)) {
                _logger.LogInformation("Unavailable command executed! from message: {content}", socketMessageCommand.Content);
                socketMessageCommand.Channel.SendMessageAsync("The requested command is unavailable");
                return Task.CompletedTask;
            }

            _logger.LogInformation("Executed command: {command}", socketMessageCommand.Content);
            return command.ExecuteCommand(socketMessageCommand, new CommandContext(_databaseContext));
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
