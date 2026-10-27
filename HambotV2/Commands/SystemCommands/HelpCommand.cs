using Discord;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>One help-list group: a command category plus its pre-rendered command lines.</summary>
    internal sealed record CommandGroup(CommandCategory Category, IReadOnlyList<string> Lines);

    /// <summary>
    /// Lists the commands available to the invoking user, or shows detailed help for one
    /// command. The list groups the commands by category — a group renders as a bold header
    /// over its alphabetized command lines, and a category with nothing visible to the invoker
    /// is omitted entirely. The list is paginated (10 commands per page, never splitting a
    /// group that fits whole); a page is requested with a numeric argument (<c>!help 2</c>) or,
    /// interactively, with the previous/next buttons attached to a multi-page list. The command
    /// is exposed on both surfaces — text and <c>/help</c> — and unknown commands and commands
    /// above the user's tier get the same generic reply, so the existence of higher-tier
    /// commands is never revealed.
    /// </summary>
    internal class HelpCommand : ICommand
    {
        public string Name => "help";
        public string Description => "List the commands you can use, or show detailed help for one command";
        public string Usage => "help [commandName | pageNumber]";
        public CommandCategory Category => CommandCategory.General;

        public ApplicationCommandDeclaration? ApplicationCommand { get; } = new(
        [
            new ApplicationCommandOption("command", "Show detailed help for one command", ApplicationCommandOptionKind.Text, Required: false),
            new ApplicationCommandOption("page", "The page number of the command list to show", ApplicationCommandOptionKind.Integer, Required: false),
        ]);

        public int DefaultPermissionTier => PermissionTier.Everyone;

        // A page holds at most 10 commands; the character budget below stays as a secondary
        // guard so a page of long lines still fits Discord's 2000-character message limit.
        internal const int MaxPageLines = 10;
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
            List<CommandGroup> groups = await GetVisibleCommandGroupsAsync(context, message.Author.Id);
            List<string> pages = BuildPages(groups);

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

            // The buttons ride along on every surface: a real channel renders them, the
            // virtual channel captures them for the interaction reply. They encode the
            // invoker, so only they can page through their own list.
            await message.Channel.SendMessageAsync(pageText, components: HelpPagination.BuildButtons(message.Author.Id, requestedPage, pages.Count));
        }

        /// <summary>The commands the user's tier permits, grouped by category in display order, alphabetized within each group. Categories with no visible command are omitted.</summary>
        private async Task<List<CommandGroup>> GetVisibleCommandGroupsAsync(ICommandContext context, ulong userId)
        {
            int userTier = context.GetUserEffectiveTier(userId);

            var linesByCategory = new Dictionary<CommandCategory, List<string>>();
            foreach (ICommand command in _commandService.Commands.Values.OrderBy(command => command.Name))
            {
                int requiredTier = await _permissionsService.GetCommandRequiredTier(context.GuildId, command, context.DbContext);
                if (requiredTier <= userTier)
                {
                    if (!linesByCategory.TryGetValue(command.Category, out List<string>? lines))
                        linesByCategory[command.Category] = lines = new List<string>();
                    lines.Add($"{context.CommandIdentifier}{command.Name} — {command.Description}");
                }
            }

            return Enum.GetValues<CommandCategory>()
                .Where(linesByCategory.ContainsKey)
                .Select(category => new CommandGroup(category, linesByCategory[category]))
                .ToList();
        }

        /// <summary>
        /// Splits the grouped command list into pages of at most <see cref="MaxPageLines"/> command lines
        /// each, never exceeding <paramref name="maxPageLength"/> characters per page (line breaks
        /// included), with one group per page-section: a group that fits within a full page's budget is
        /// never split across pages, a group that alone exceeds it is split with its header repeated on
        /// each continuation page, and a single line always forms a page even if it alone exceeds the budget.
        /// </summary>
        internal static List<string> BuildPages(IReadOnlyList<CommandGroup> groups, int maxPageLength = MaxPageLength)
        {
            var pages = new List<string>();
            var lines = new List<string>();
            int commandCount = 0;
            int length = 0;

            int AdditionalLength(string line) => line.Length + (lines.Count > 0 ? Environment.NewLine.Length : 0);

            void AddLine(string line)
            {
                length += AdditionalLength(line);
                lines.Add(line);
            }

            void Flush()
            {
                if (lines.Count == 0)
                    return;
                pages.Add(string.Join(Environment.NewLine, lines));
                lines.Clear();
                commandCount = 0;
                length = 0;
            }

            foreach (CommandGroup group in groups)
            {
                if (group.Lines.Count == 0)
                    continue;

                string header = $"**{CommandCategories.Header(group.Category)}**";
                string block = header + Environment.NewLine + string.Join(Environment.NewLine, group.Lines);

                if (group.Lines.Count <= MaxPageLines && block.Length <= maxPageLength)
                {
                    // The blank separator line adds two line breaks to the page's joined length.
                    int appendedLength = length + (lines.Count > 0 ? 2 * Environment.NewLine.Length : 0) + block.Length;
                    if (commandCount > 0 && (commandCount + group.Lines.Count > MaxPageLines || appendedLength > maxPageLength))
                        Flush();

                    if (lines.Count > 0)
                        AddLine("");
                    AddLine(header);
                    foreach (string line in group.Lines)
                    {
                        AddLine(line);
                        commandCount++;
                    }
                }
                else
                {
                    if (lines.Count > 0)
                        AddLine("");
                    AddLine(header);
                    foreach (string line in group.Lines)
                    {
                        if (commandCount >= MaxPageLines || (commandCount > 0 && length + AdditionalLength(line) > maxPageLength))
                        {
                            Flush();
                            AddLine(header);
                        }

                        AddLine(line);
                        commandCount++;
                    }
                }
            }

            Flush();
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
