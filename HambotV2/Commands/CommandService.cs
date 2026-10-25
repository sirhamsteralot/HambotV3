using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

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
            // Registration is the single chokepoint for the command contract (roadmap T17): a
            // command without help text or with an invalid tier/default never enters the registry.
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentException("Command names must not be empty.", nameof(commandName));

            if (commandName != commandName.ToLowerInvariant())
                throw new ArgumentException($"Command name '{commandName}' must be lowercase (lookup lowercases input).", nameof(commandName));

            if (string.IsNullOrWhiteSpace(command?.Description))
                throw new ArgumentException($"Command '{commandName}' must declare a non-empty Description for the help system.", nameof(command));

            if (string.IsNullOrWhiteSpace(command.Usage))
                throw new ArgumentException($"Command '{commandName}' must declare a non-empty Usage for the help system.", nameof(command));

            if (!PermissionTier.IsValid(command.DefaultPermissionTier))
                throw new ArgumentException($"Command '{commandName}' declares invalid default permission tier {command.DefaultPermissionTier}.", nameof(command));

            Commands.Add(commandName, command);
        }

        public bool TryGetCommand(string content, string identifier, [NotNullWhen(true)] out ICommand? command)
        {
            if (!CommandsRegistered)
                RegisterCommands();

            command = null;

            if (!CommandParsing.TryGetCommandName(content, identifier, out string commandString))
                return false;

            if (!Commands.TryGetValue(commandString, out ICommand? selectedCommand))
                return false;

            command = selectedCommand;
            return true;
        }

        public bool IsIdentifierOnlyMessage(string content, string identifier)
        {
            return CommandParsing.IsIdentifierOnly(content, identifier);
        }
    }
}
