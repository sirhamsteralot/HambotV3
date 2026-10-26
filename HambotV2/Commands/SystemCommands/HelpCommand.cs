using Discord;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Lists the commands available to the invoking user, or shows detailed help for one
    /// command. The list is paginated when it would exceed Discord's message length limit;
    /// pages are requested with a numeric argument (<c>!help 2</c>). Unknown commands and commands
    /// above the user's tier get the same generic reply, so the existence of higher-tier commands
    /// is never revealed.
    /// </summary>
    internal class HelpCommand : ICommand
    {
        public string Name => "help";
        public string Description => "List the commands you can use, or show detailed help for one command";
        public string Usage => "help [commandName | pageNumber]";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        // Discord caps a message at 2000 characters. The budget applies to the command lines only;
        // the page header and page-hint footer are short enough that the full message stays below the cap.
        internal const int MaxPageLength = 1800;

        private readonly ICommandService _commandService;
        private readonly IPermissionsService _permissionsService;

        public HelpCommand(ICommandService commandService, IPermissionsService permissionsService)
        {
            _commandService = commandService;
            _permissionsService = permissionsService;
        }

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            var arguments = CommandArguments.Parse(message.Content);

            if (arguments.Get(0) is string firstArgument)
            {
                if (int.TryParse(firstArgument, out int requestedPage))
                    await SendCommandList(message, context, requestedPage);
                else
                    await SendCommandHelp(message, context, firstArgument);
            }
            else
            {
                await SendCommandList(message, context, requestedPage: 1);
            }
        }

        private async Task SendCommandList(IMessage message, ICommandContext context, int requestedPage)
        {
            int userTier = context.GetUserEffectiveTier(message.Author.Id);

            var lines = new List<string>();
            foreach (ICommand command in _commandService.Commands.Values.OrderBy(command => command.Name))
            {
                int requiredTier = await _permissionsService.GetCommandRequiredTier(context.GuildId, command, context.DbContext);
                if (requiredTier <= userTier)
                    lines.Add($"{context.CommandIdentifier}{command.Name} — {command.Description}");
            }

            List<string> pages = BuildPages(lines);

            if (requestedPage < 1 || requestedPage > pages.Count)
            {
                await message.Channel.SendMessageAsync(
                    pages.Count == 0
                        ? "There are no commands available to you"
                        : $"There is no page {requestedPage}; the command list has {pages.Count} page(s)");
                return;
            }

            bool multiPage = pages.Count > 1;
            string header = multiPage
                ? $"Available commands (page {requestedPage} of {pages.Count}):"
                : "Available commands:";

            string pageText = header + Environment.NewLine + pages[requestedPage - 1];
            if (multiPage)
                pageText += Environment.NewLine + $"Use {context.CommandIdentifier}help <pageNumber> to see another page.";

            await message.Channel.SendMessageAsync(pageText);
        }

        /// <summary>
        /// Splits the command lines into pages of at most <paramref name="maxPageLength"/> characters each
        /// (line breaks included), preserving alphabetical order. A single line always forms a page even
        /// if it alone exceeds the budget.
        /// </summary>
        internal static List<string> BuildPages(IReadOnlyList<string> lines, int maxPageLength = MaxPageLength)
        {
            var pages = new List<string>();
            var current = new List<string>();
            int currentLength = 0;

            foreach (string line in lines)
            {
                int additionalLength = line.Length + (current.Count > 0 ? Environment.NewLine.Length : 0);
                if (current.Count > 0 && currentLength + additionalLength > maxPageLength)
                {
                    pages.Add(string.Join(Environment.NewLine, current));
                    current.Clear();
                    currentLength = 0;
                }

                current.Add(line);
                currentLength += line.Length + (current.Count > 1 ? Environment.NewLine.Length : 0);
            }

            if (current.Count > 0)
                pages.Add(string.Join(Environment.NewLine, current));

            return pages;
        }

        private async Task SendCommandHelp(IMessage message, ICommandContext context, string requestedCommandName)
        {
            if (!_commandService.Commands.TryGetValue(requestedCommandName.ToLowerInvariant(), out ICommand? command) || command is null)
            {
                await SendGenericUnavailableReply(message);
                return;
            }

            int userTier = context.GetUserEffectiveTier(message.Author.Id);
            int requiredTier = await _permissionsService.GetCommandRequiredTier(context.GuildId, command, context.DbContext);

            if (requiredTier > userTier)
            {
                // Deliberately identical to the reply for unknown commands: the reply must
                // not reveal whether the command exists at a higher tier.
                await SendGenericUnavailableReply(message);
                return;
            }

            await message.Channel.SendMessageAsync(
                $"{context.CommandIdentifier}{command.Name} — {command.Description}" + Environment.NewLine +
                $"Usage: {context.CommandIdentifier}{command.Usage}" + Environment.NewLine +
                $"Required permission: {PermissionTier.GetName(requiredTier)} (tier {requiredTier})");
        }

        private static Task SendGenericUnavailableReply(IMessage message)
        {
            return message.Channel.SendMessageAsync("The requested command is unavailable");
        }
    }
}
