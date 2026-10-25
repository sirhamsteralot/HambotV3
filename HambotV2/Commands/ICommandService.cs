using System.Diagnostics.CodeAnalysis;

namespace HambotV2.Commands
{
    internal interface ICommandService
    {
        /// <summary>All registered commands, keyed by their lowercase name.</summary>
        Dictionary<string, ICommand> Commands { get; }

        /// <summary>True once the built-in commands have been registered.</summary>
        bool CommandsRegistered { get; }

        /// <summary>True when the message is the command identifier (plus whitespace) only; such messages are ignored silently.</summary>
        bool IsIdentifierOnlyMessage(string content, string identifier);

        bool TryGetCommand(string content, string identifier, [NotNullWhen(true)] out ICommand? command);

        void RegisterCommand(string commandName, ICommand command);

        void RegisterCommands();
    }
}
