using Discord;
using HambotV2.Commands;
using HambotV2.Connections;
using HambotV2.Maintenance;
using HambotV2.Watchdog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HambotV2.Mcp
{
    /// <summary>
    /// The MCP tool implementations: thin wrappers over the existing components —
    /// the shared client's status, guild, and member caches, the presence service, the REST
    /// message surface, and the virtual invocation seam — so nothing re-implements command or
    /// data logic. Every call logs one structured event with the tool name and outcome, without
    /// message content; failures are contained and returned as error results.
    /// </summary>
    internal sealed class McpTools : IMcpTools
    {
        /// <summary>The cap of <c>get_recent_messages</c>.</summary>
        internal const int MaxRecentMessages = 50;

        /// <summary>The cap of <c>send_message</c>, Discord's own plain-text limit.</summary>
        internal const int MaxSendMessageLength = 2000;

        /// <summary>The cap of <c>get_user_quotes</c>.</summary>
        internal const int MaxUserQuotes = 50;

        /// <summary>The cap of <c>get_user_messages</c>.</summary>
        internal const int MaxUserMessages = 50;

        /// <summary>The cap of <c>search_users</c>.</summary>
        internal const int MaxUserSearchResults = 25;

        /// <summary>How many custom-response names <c>get_user_profile</c> lists before the "+N more" count.</summary>
        internal const int MaxModifiedResponsesListed = 10;

        private readonly IMcpDiscordClient _discord;
        private readonly IPresenceService _presence;
        private readonly IGatewayStatus _gateway;
        private readonly ICommandService _commands;
        private readonly IVirtualCommandRunner _commandRunner;
        private readonly IMaintenanceMode _maintenanceMode;
        private readonly IServiceProvider _services;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<McpTools> _logger;
        private readonly DateTimeOffset _startedAtUtc;

        public McpTools(
            IMcpDiscordClient discord,
            IPresenceService presence,
            IGatewayStatus gateway,
            ICommandService commands,
            IVirtualCommandRunner commandRunner,
            IMaintenanceMode maintenanceMode,
            IServiceProvider services,
            TimeProvider timeProvider,
            ILogger<McpTools> logger)
        {
            _discord = discord;
            _presence = presence;
            _gateway = gateway;
            _commands = commands;
            _commandRunner = commandRunner;
            _maintenanceMode = maintenanceMode;
            _services = services;
            _timeProvider = timeProvider;
            _logger = logger;
            _startedAtUtc = timeProvider.GetUtcNow();
        }

        public McpToolResult BotStatus()
        {
            return Invoke("bot_status", () =>
            {
                PresenceActivity? activity = _presence.CurrentActivity;
                return McpToolResult.Ok(
                    $"Version: {BotVersion.Current}\n" +
                    $"Uptime: {FormatUptime(_timeProvider.GetUtcNow() - _startedAtUtc)}\n" +
                    $"Gateway: {_gateway.State}\n" +
                    $"Guilds: {_discord.GetGuilds().Count}\n" +
                    $"Presence: {(activity is null ? "none" : $"{PresenceActivityTypes.GetLabel(activity.Type)} {activity.Text}")}\n" +
                    $"Bio: {_presence.CurrentBio ?? "none"}");
            });
        }

        public McpToolResult ListGuilds()
        {
            return Invoke("list_guilds", () =>
            {
                IReadOnlyList<DiscordGuildSummary> guilds = _discord.GetGuilds();
                return McpToolResult.Ok(guilds.Count == 0
                    ? "The bot is not a member of any guild."
                    : string.Join("\n", guilds.Select(guild => $"{guild.Id} {guild.Name}")));
            });
        }

        public McpToolResult ListChannels(ulong guildId)
        {
            return Invoke("list_channels", () =>
            {
                IReadOnlyList<DiscordChannelSummary>? channels = _discord.GetGuildChannels(guildId);
                if (channels is null)
                    return McpToolResult.Error(UnknownGuild(guildId));

                return McpToolResult.Ok(channels.Count == 0
                    ? "The guild has no accessible channels."
                    : string.Join("\n", channels.Select(channel => $"{channel.Id} {channel.Name} ({channel.Type})")));
            });
        }

        public McpToolResult ListCommands()
        {
            return Invoke("list_commands", () =>
            {
                // Same lazy registration as the pipeline's lookup, so the list is complete
                // even before any message has triggered it.
                if (!_commands.CommandsRegistered)
                    _commands.RegisterCommands();

                return McpToolResult.Ok(
                    "Registered commands (for run_command, without the identifier; guilds may override the required tier):\n" +
                    string.Join("\n", _commands.Commands.Values
                        .OrderBy(command => command.Name)
                        .Select(command => $"{command.Name} (tier {command.DefaultPermissionTier}) — {command.Description} | usage: {command.Usage}")));
            });
        }

        public Task<McpToolResult> SetPresenceAsync(string type, string text)
        {
            return InvokeAsync("set_presence", async () =>
            {
                if (!PresenceActivityTypes.TryParse(type, out ActivityType activityType))
                    return McpToolResult.Error(
                        $"Unknown activity type '{type}' — supported types: {string.Join(", ", PresenceActivityTypes.SupportedNames)}.");

                return await ApplyActivityAsync(activityType, text);
            });
        }

        private async Task<McpToolResult> ApplyActivityAsync(ActivityType activityType, string text)
        {
            if (text.Contains('\n') || text.Contains('\r'))
                return McpToolResult.Error("The activity text must be a single line.");

            string trimmed = text.Trim();
            if (trimmed.Length == 0)
                return McpToolResult.Error("The activity text is missing.");

            if (trimmed.Length > ProfileLimits.MaxActivityTextLength)
                return McpToolResult.Error(
                    $"The activity text must be at most {ProfileLimits.MaxActivityTextLength} characters (got {trimmed.Length}).");

            if (!await _presence.ApplyActivityAsync(activityType, trimmed))
                return McpToolResult.Error("Failed to update the activity — a Discord error occurred.");

            return McpToolResult.Ok($"Activity set: {PresenceActivityTypes.GetLabel(activityType)} {trimmed}.");
        }

        public Task<McpToolResult> SetBioAsync(string text)
        {
            return InvokeAsync("set_bio", async () =>
            {
                if (text.Trim().Length == 0)
                    return McpToolResult.Error("The bio text is missing.");

                if (text.Length > ProfileLimits.MaxBioLength)
                    return McpToolResult.Error($"The bio must be at most {ProfileLimits.MaxBioLength} characters (got {text.Length}).");

                if (!await _presence.ApplyBioAsync(text))
                    return McpToolResult.Error("Failed to update the bio — a Discord error occurred.");

                return McpToolResult.Ok($"The bio was set to:\n{text}");
            });
        }

        public Task<McpToolResult> GetRecentMessagesAsync(ulong channelId, int limit)
        {
            return InvokeAsync("get_recent_messages", async () =>
            {
                int capped = CapLimit(limit, MaxRecentMessages);

                IReadOnlyList<DiscordChannelMessage>? messages = await _discord.GetRecentMessagesAsync(channelId, capped);
                if (messages is null)
                    return McpToolResult.Error($"The bot has no accessible channel with id {channelId}.");

                return McpToolResult.Ok(messages.Count == 0
                    ? "No recent messages in that channel."
                    : string.Join("\n", messages.Select(FormatMessage)));
            });
        }

        public Task<McpToolResult> SendMessageAsync(ulong channelId, string text)
        {
            return InvokeAsync("send_message", async () =>
            {
                if (text.Trim().Length == 0)
                    return McpToolResult.Error("The message text is missing.");

                if (text.Length > MaxSendMessageLength)
                    return McpToolResult.Error($"The message must be at most {MaxSendMessageLength} characters (got {text.Length}).");

                if (!await _discord.TrySendMessageAsync(channelId, text))
                    return McpToolResult.Error($"The bot has no accessible channel with id {channelId}.");

                return McpToolResult.Ok($"Message sent to channel {channelId}.");
            });
        }

        public Task<McpToolResult> RunCommandAsync(ulong guildId, string commandLine)
        {
            return InvokeAsync("run_command", async () =>
            {
                if (string.IsNullOrWhiteSpace(commandLine))
                    return McpToolResult.Error("The command line is missing.");

                // A guild-scoped origin channel carries the guild's data into the pipeline
                // (identifier, tiers, cooldown keys); replies are captured by the seam.
                IMessageChannel? channel = _discord.GetGuildTextChannel(guildId);
                if (channel is null)
                    return McpToolResult.Error($"The bot has no accessible text channel in guild {guildId} (unknown guild?).");

                IUser? botUser = _discord.CurrentUser;
                if (botUser is null)
                    return McpToolResult.Error("The bot's Discord user is not available yet — the gateway is still connecting.");

                // The invocation acts as the operator's agent: the bot's own user raised to
                // tier, with cooldowns and audit keyed on that same user.
                var invocation = new VirtualCommandInvocation(
                    botUser, channel, commandLine.Trim(), _timeProvider.GetUtcNow(), OperatorAgentUserId: botUser.Id);

                VirtualCommandResult result = await _commandRunner.RunAsync(invocation);

                return result.Replies.Count > 0
                    ? McpToolResult.Ok(string.Join("\n", result.Replies.Select(reply => reply.Text)))
                    : McpToolResult.Ok($"Command finished with outcome {result.Outcome} (no reply).");
            });
        }

        public Task<McpToolResult> SearchUsersAsync(ulong guildId, string query, int limit)
        {
            return InvokeAsync("search_users", async () =>
            {
                if (_maintenanceMode.IsActive)
                    return McpToolResult.Error(CommandHandler.MaintenanceNotice);

                string trimmed = query.Trim();
                if (trimmed.Length == 0)
                    return McpToolResult.Error("The search query is missing.");

                int capped = CapLimit(limit, MaxUserSearchResults);

                IReadOnlyList<DiscordUserSummary>? users = await _discord.SearchUsersAsync(guildId, trimmed, capped);
                if (users is null)
                    return McpToolResult.Error(UnknownGuild(guildId));

                return McpToolResult.Ok(users.Count == 0
                    ? $"No members of guild {guildId} match '{trimmed}'."
                    : string.Join("\n", users.Select(user =>
                        $"{user.Id} {user.Username}" +
                        (user.DisplayName is string display && display != user.Username ? $" ({display})" : "") +
                        (user.Nickname is string nickname ? $" [nickname: {nickname}]" : ""))));
            });
        }

        public Task<McpToolResult> GetUserProfileAsync(ulong guildId, ulong userId)
        {
            return InvokeAsync("get_user_profile", async () =>
            {
                if (_maintenanceMode.IsActive)
                    return McpToolResult.Error(CommandHandler.MaintenanceNotice);

                if (_discord.GetGuildChannels(guildId) is null)
                    return McpToolResult.Error(UnknownGuild(guildId));

                DiscordMemberSummary? member = await _discord.GetGuildMemberAsync(guildId, userId);

                // A fresh scoped transient context per call — the singleton tool layer never holds a DbContext.
                using IServiceScope scope = _services.CreateScope();
                IDatabaseContext database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();
                DateTimeOffset now = _timeProvider.GetUtcNow();

                var user = await database.Users.FindAsync(guildId, userId);
                DatabaseContext.BotBan? ban = await BotBans.FindActiveAsync(database, guildId, userId, now);
                var quotes = database.Quotes.Where(quote => quote.GuildId == guildId && quote.AddedByDiscordId == userId);
                int quoteCount = await quotes.CountAsync();
                DatabaseContext.Quote? latestQuote = await quotes.OrderByDescending(quote => quote.Number).FirstOrDefaultAsync();
                var responses = database.CustomResponses
                    .Where(response => response.GuildId == guildId && response.LastModifiedByDiscordId == userId)
                    .OrderByDescending(response => response.UpdatedAt);
                int responseCount = await responses.CountAsync();
                List<string> responseNames = await responses
                    .Take(MaxModifiedResponsesListed)
                    .Select(response => response.Name)
                    .ToListAsync();

                List<string> lines = [];
                if (member is null)
                {
                    lines.Add($"Member {userId} is no longer in guild {guildId} (Discord identity unavailable).");
                }
                else
                {
                    lines.Add($"Member {userId}: {member.Username}" +
                        (member.DisplayName is string display && display != member.Username ? $" ({display})" : "") +
                        (member.Nickname is string nickname ? $" [nickname: {nickname}]" : ""));
                    if (member.Roles.Count > 0)
                        lines.Add($"Roles: {string.Join(", ", member.Roles)}");
                    if (member.JoinedAt is DateTimeOffset joinedAt)
                        lines.Add($"Joined: {joinedAt:yyyy-MM-dd HH:mm} UTC");
                }

                lines.Add($"Permission tier: {user?.PermissionTier ?? 0}");
                lines.Add(ban is null
                    ? "Bot ban: none"
                    : $"Bot ban: by {ban.BannedByDiscordId} on {ban.CreatedAt:yyyy-MM-dd HH:mm} UTC, {BotBans.DescribeLifetime(ban, now)}");
                lines.Add(latestQuote is null
                    ? "Quotes added: 0"
                    : $"Quotes added: {quoteCount}; most recent: #{latestQuote.Number} on {latestQuote.CreatedAt:yyyy-MM-dd HH:mm} UTC");
                lines.Add(responseCount == 0
                    ? "Custom responses last modified: none"
                    : $"Custom responses last modified: {responseCount}" +
                        (responseNames.Count < responseCount ? $" (+{responseCount - responseNames.Count} more)" : "") +
                        $": {string.Join(", ", responseNames)}");

                return McpToolResult.Ok(string.Join("\n", lines));
            });
        }

        public Task<McpToolResult> GetUserQuotesAsync(ulong guildId, ulong userId)
        {
            return InvokeAsync("get_user_quotes", async () =>
            {
                if (_maintenanceMode.IsActive)
                    return McpToolResult.Error(CommandHandler.MaintenanceNotice);

                if (_discord.GetGuildChannels(guildId) is null)
                    return McpToolResult.Error(UnknownGuild(guildId));

                using IServiceScope scope = _services.CreateScope();
                IDatabaseContext database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

                List<DatabaseContext.Quote> quotes = await database.Quotes
                    .Where(quote => quote.GuildId == guildId && quote.AddedByDiscordId == userId)
                    .OrderByDescending(quote => quote.Number)
                    .ToListAsync();

                if (quotes.Count == 0)
                    return McpToolResult.Ok($"Member {userId} has not added any quotes in guild {guildId}.");

                IEnumerable<string> lines = quotes
                    .Take(MaxUserQuotes)
                    .Select(quote => $"#{quote.Number}: {quote.Text}");
                if (quotes.Count > MaxUserQuotes)
                    lines = lines.Append($"+{quotes.Count - MaxUserQuotes} more");

                return McpToolResult.Ok(string.Join("\n", lines));
            });
        }

        public Task<McpToolResult> GetUserMessagesAsync(ulong guildId, ulong userId, int limit)
        {
            return InvokeAsync("get_user_messages", async () =>
            {
                if (_maintenanceMode.IsActive)
                    return McpToolResult.Error(CommandHandler.MaintenanceNotice);

                IReadOnlyList<ulong>? channelIds = _discord.GetGuildMessageChannelIds(guildId);
                if (channelIds is null)
                    return McpToolResult.Error(UnknownGuild(guildId));

                int capped = CapLimit(limit, MaxUserMessages);

                // One page per readable channel; a channel that fails to load is skipped,
                // so a partial result beats a failed call.
                var matches = new List<(DiscordChannelMessage Message, ulong ChannelId)>();
                foreach (ulong channelId in channelIds)
                {
                    IReadOnlyList<DiscordChannelMessage>? messages = await _discord.GetRecentMessagesAsync(channelId, capped);
                    if (messages is null)
                        continue;

                    matches.AddRange(messages
                        .Where(message => message.AuthorId == userId)
                        .Select(message => (message, channelId)));
                }

                if (matches.Count == 0)
                    return McpToolResult.Ok($"Member {userId} has no recent messages in the bot's readable channels of guild {guildId}.");

                IEnumerable<string> lines = matches
                    .OrderByDescending(match => match.Message.Timestamp)
                    .Take(capped)
                    .Select(match => $"[{match.Message.Timestamp:yyyy-MM-dd HH:mm} UTC] (channel {match.ChannelId}) {match.Message.Content}");

                return McpToolResult.Ok(string.Join("\n", lines));
            });
        }

        private static string FormatMessage(DiscordChannelMessage message)
        {
            return $"[{message.Timestamp:yyyy-MM-dd HH:mm} UTC] {message.AuthorName} ({message.AuthorId}): {message.Content}";
        }

        /// <summary>The guild access every lookup reports when the guild is not in the bot's cache.</summary>
        private static string UnknownGuild(ulong guildId) => $"The bot has no accessible guild with id {guildId}.";

        private static int CapLimit(int limit, int cap) => limit <= 0 ? cap : Math.Min(limit, cap);

        private static string FormatUptime(TimeSpan uptime) => uptime.Days > 0
            ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
            : uptime.Hours > 0
                ? $"{uptime.Hours}h {uptime.Minutes}m"
                : $"{uptime.Minutes}m";

        /// <summary>The failure containment: one structured event per call, failures returned as error results, never thrown.</summary>
        private McpToolResult Invoke(string toolName, Func<McpToolResult> action)
        {
            try
            {
                McpToolResult result = action();
                _logger.LogInformation("MCP tool {Tool} invoked with outcome {Outcome}", toolName, result.IsError ? "error" : "success");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP tool {Tool} failed", toolName);
                return McpToolResult.Error($"The '{toolName}' tool call failed — see the bot's logs for details.");
            }
        }

        private async Task<McpToolResult> InvokeAsync(string toolName, Func<Task<McpToolResult>> action)
        {
            try
            {
                McpToolResult result = await action();
                _logger.LogInformation("MCP tool {Tool} invoked with outcome {Outcome}", toolName, result.IsError ? "error" : "success");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP tool {Tool} failed", toolName);
                return McpToolResult.Error($"The '{toolName}' tool call failed — see the bot's logs for details.");
            }
        }
    }
}
