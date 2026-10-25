using Discord;
using HambotV2.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Lists the commands available to the invoking user, or shows detailed help for one command
    /// (FR-38 to FR-40). Unknown commands and commands above the user's tier get
    /// the same generic reply, so the existence of higher-tier commands is never revealed.
    /// </summary>
    internal class HelpCommand : ICommand
    {
        public string Name => "help";
        public string Description => "List the commands you can use, or show detailed help for one command";
        public string Usage => "help [commandName]";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        private readonly ICommandService _commandService;
        private readonly IPermissionsService _permissionsService;

        public HelpCommand(ICommandService commandService, IPermissionsService permissionsService)
        {
            _commandService = commandService;
            _permissionsService = permissionsService;
        }

        public async Task ExecuteCommand(IMessage socketMessage, ICommandContext context)
        {
            var arguments = CommandArguments.Parse(socketMessage.Content);

            if (arguments.Get(0) is string requestedCommandName)
                await SendCommandHelp(socketMessage, context, requestedCommandName);
            else
                await SendCommandList(socketMessage, context);
        }

        private async Task SendCommandList(IMessage socketMessage, ICommandContext context)
        {
            int userTier = context.GetUserEffectiveTier(socketMessage.Author.Id);

            var lines = new List<string> { "Available commands:" };
            foreach (ICommand command in _commandService.Commands.Values.OrderBy(command => command.Name))
            {
                int requiredTier = await _permissionsService.GetCommandRequiredTier(context.GuildId, command, context.DbContext);
                if (requiredTier <= userTier)
                    lines.Add($"{context.CommandIdentifier}{command.Name} — {command.Description}");
            }

            await socketMessage.Channel.SendMessageAsync(string.Join(Environment.NewLine, lines));
        }

        private async Task SendCommandHelp(IMessage socketMessage, ICommandContext context, string requestedCommandName)
        {
            if (!_commandService.Commands.TryGetValue(requestedCommandName.ToLowerInvariant(), out ICommand? command) || command is null)
            {
                await SendGenericUnavailableReply(socketMessage);
                return;
            }

            int userTier = context.GetUserEffectiveTier(socketMessage.Author.Id);
            int requiredTier = await _permissionsService.GetCommandRequiredTier(context.GuildId, command, context.DbContext);

            if (requiredTier > userTier)
            {
                // Deliberately identical to the reply for unknown commands (FR-40): the reply must
                // not reveal whether the command exists at a higher tier.
                await SendGenericUnavailableReply(socketMessage);
                return;
            }

            await socketMessage.Channel.SendMessageAsync(
                $"{context.CommandIdentifier}{command.Name} — {command.Description}" + Environment.NewLine +
                $"Usage: {context.CommandIdentifier}{command.Usage}" + Environment.NewLine +
                $"Required permission: {PermissionTier.GetName(requiredTier)} (tier {requiredTier})");
        }

        private static async Task SendGenericUnavailableReply(IMessage socketMessage)
        {
            await socketMessage.Channel.SendMessageAsync("The requested command is unavailable");
        }
    }
}
