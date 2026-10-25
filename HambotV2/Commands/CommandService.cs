using System.Diagnostics.CodeAnalysis;
using HambotV2.Scheduling;

namespace HambotV2.Commands
{
    internal class CommandService : ICommandService
    {
        /// <summary>Shared with the scheduled-message commands and the delivery loops; production DI supplies the one singleton.</summary>
        private readonly IScheduledDeliverySignal _deliverySignal;

        /// <summary>
        /// The signal defaults to an isolated instance, which keeps the registry usable in tests
        /// that do not exercise scheduled delivery (the default's notifications simply have no
        /// listener).
        /// </summary>
        public CommandService(IScheduledDeliverySignal? deliverySignal = null)
        {
            _deliverySignal = deliverySignal ?? new ScheduledDeliverySignal();
        }

        public Dictionary<string, ICommand> Commands { get; } = new();

        /// <summary>True once the built-in commands have been registered; guards the lazy registration in <see cref="TryGetCommand"/>.</summary>
        public bool CommandsRegistered { get; private set; }

        public void RegisterCommands()
        {
            RegisterAllCommands.RegisterCommands(this, _deliverySignal);
            CommandsRegistered = true;
        }

        public void RegisterCommand(string commandName, ICommand command)
        {
            // The single chokepoint for the command contract: a command without help text, with a
            // non-lowercase name, or with an invalid default tier never enters the registry.
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentException("Command names must not be empty.", nameof(commandName));

            if (commandName != commandName.ToLowerInvariant())
                throw new ArgumentException($"Command name '{commandName}' must be lowercase (lookup lowercases input).", nameof(commandName));

            if (string.IsNullOrWhiteSpace(command.Description))
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
            return CommandParsing.TryGetCommandName(content, identifier, out string commandName)
                && Commands.TryGetValue(commandName, out command);
        }

        public bool IsIdentifierOnlyMessage(string content, string identifier)
        {
            return CommandParsing.IsIdentifierOnly(content, identifier);
        }
    }
}
