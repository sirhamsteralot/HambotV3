using Discord;
using HambotV2.Connections;

namespace HambotV2.Commands.ResponseCommands
{
    /// <summary>
    /// Creates a custom response of the invoking guild, or updates an existing one's content
    /// (FR-56); moderator by default, declining in direct messages (FR-59). Names follow FR-52
    /// and may not collide with a registered built-in (FR-53). Content is text only (FR-60):
    /// invocations carrying file or image attachments are rejected and nothing is stored — links
    /// in the content are ordinary text and are stored verbatim.
    /// </summary>
    internal class AddResponseCommand : ICommand
    {
        /// <summary>Maximum response content length (FR-56).</summary>
        internal const int MaxContentLength = 1000;

        private readonly ICommandService _commandService;

        public AddResponseCommand(ICommandService commandService)
        {
            _commandService = commandService;
        }

        public string Name => "addresponse";
        public string Description => "Create a custom response for this server, or update an existing one's text";
        public string Usage => "addresponse <name> <content...>";
        public int DefaultPermissionTier => PermissionTier.Moderator;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Responses are per-guild state; the DM scope (FR-25/FR-59) has no guild to define them in.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            // Text-only content (FR-60): the whole invocation is rejected before anything is validated
            // or stored, so an attachment can never sneak a file into a response.
            if (message.Attachments.Count > 0)
            {
                await message.Channel.SendMessageAsync("Custom responses are text only — remove the file attachment and put a link in the content instead");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (!arguments.TryGetString(0, out string? name))
            {
                await message.Channel.SendMessageAsync($"Could not parse a response name from '{arguments.Get(0)}'. Usage: !{Usage}");
                return;
            }

            if (!CustomResponseNames.IsValid(name))
            {
                await message.Channel.SendMessageAsync($"Invalid response name '{name}'. Names are 1–32 characters: lowercase letters, digits, hyphens, and underscores");
                return;
            }

            // Built-in commands always win dispatch (FR-53), so their names are reserved up front —
            // including built-ins added later, since dispatch consults the registry first regardless.
            if (_commandService.Commands.ContainsKey(name))
            {
                await message.Channel.SendMessageAsync($"'{name}' is the name of a built-in command and cannot be used for a response");
                return;
            }

            string? content = arguments.GetJoined(1);
            if (string.IsNullOrEmpty(content))
            {
                await message.Channel.SendMessageAsync("The response content must not be empty");
                return;
            }

            if (content.Length > MaxContentLength)
            {
                await message.Channel.SendMessageAsync($"The response content must be at most {MaxContentLength} characters long");
                return;
            }

            var response = await context.DbContext.CustomResponses.FindAsync(guildId, name);
            if (response is null)
            {
                context.DbContext.CustomResponses.Add(new DatabaseContext.CustomResponse
                {
                    GuildId = guildId,
                    Name = name,
                    Content = content,
                    LastModifiedByDiscordId = message.Author.Id,
                    CreatedAt = message.Timestamp.UtcDateTime,
                    UpdatedAt = message.Timestamp.UtcDateTime
                });
                await context.DbContext.SaveChangesAsync();
                await message.Channel.SendMessageAsync($"Created the response '{name}'");
                return;
            }

            // Create-or-update (FR-56): an existing name updates the content and the
            // modification fields; created/updated timestamps keep the original creation time.
            response.Content = content;
            response.LastModifiedByDiscordId = message.Author.Id;
            response.UpdatedAt = message.Timestamp.UtcDateTime;
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Updated the response '{name}'");
        }
    }
}
