using Discord.WebSocket;
using HambotV2.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Commands
{
    internal class CommandService : ICommandService
    {
        public Dictionary<string, ICommand> Commands { get; private set; }

        private ILogger<CommandHandler> _logger;
        private IConfiguration _configuration;

        public CommandService(ILogger<CommandHandler> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            Commands = new Dictionary<string, ICommand>();
        }

        public void RegisterCommand(string commandName, ICommand command)
        {
            Commands.Add(commandName, command);
        }

        public bool TryGetCommand(SocketUser author, string content, out ICommand command)
        {
            string[] strings = content.Split(_configuration["BotConfiguration:CommandIdentifier"]);

            string commandString = strings[0].Substring(1,strings[0].Length - 1).ToLower();

            if (Commands.TryGetValue(commandString, out ICommand selectedCommand))
            {
                if (selectedCommand.IsWhitelist)
                {
                    if (selectedCommand.UserIdsList.Contains(author.Id))
                    {
                        command = selectedCommand;
                        return true;
                    }
                } else
                {
                    if (!selectedCommand.UserIdsList.Contains(author.Id))
                    {
                        command = selectedCommand;
                        return true;
                    }
                }
            }

            command = null;
            return false;
        }
    }
}
