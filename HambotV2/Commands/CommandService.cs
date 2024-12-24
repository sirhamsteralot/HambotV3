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

        private ILogger<CommandService> _logger;
        private IConfiguration _configuration;
        public bool CommandsRegistered { get; set; } = false;

        public CommandService(ILogger<CommandService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            Commands = new Dictionary<string, ICommand>();
        }

        public void RegisterCommands()
        {
            RegisterAllCommands.RegisterCommands(this);
            CommandsRegistered = true;
        }

        public void RegisterCommand(string commandName, ICommand command)
        {
            Commands.Add(commandName, command);
        }

        public bool TryGetCommand(string content, out ICommand command)
        {
            if (!CommandsRegistered)
                RegisterCommands();

            string[] strings = content.Trim().Split(" ");

            string commandString = strings[0].Substring(1,strings[0].Length - 1).ToLower();

            if (Commands.TryGetValue(commandString, out ICommand selectedCommand))
            {
                command = selectedCommand;
                return true;
            }

            command = null;
            return false;
        }
    }
}
