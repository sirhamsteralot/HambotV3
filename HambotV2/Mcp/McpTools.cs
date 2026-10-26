using Discord;
using HambotV2.Commands;
using HambotV2.Connections;
using HambotV2.Watchdog;

namespace HambotV2.Mcp
{
    /// <summary>
    /// The MCP tool implementations: thin wrappers over the existing components —
    /// the shared client's status and guild cache, the presence service, the REST message surface,
    /// and the virtual invocation seam — so nothing re-implements command or data logic. Every
    /// call logs one structured event with the tool name and outcome, without message content;
    /// failures are contained and returned as error results.
    /// </summary>
    internal sealed class McpTools : IMcpTools
    {
        /// <summary>The cap of <c>get_recent_messages</c>.</summary>
        internal const int MaxRecentMessages = 50;

        /// <summary>The cap of <c>send_message</c>, Discord's own plain-text limit.</summary>
        internal const int MaxSendMessageLength = 2000;

        private readonly IMcpDiscordClient _discord;
        private readonly IPresenceService _presence;
        private readonly IGatewayStatus _gateway;
        private readonly ICommandService _commands;
        private readonly IVirtualCommandRunner _commandRunner;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<McpTools> _logger;
        private readonly DateTimeOffset _startedAtUtc;

        public McpTools(
            IMcpDiscordClient discord,
            IPresenceService presence,
            IGatewayStatus gateway,
            ICommandService commands,
            IVirtualCommandRunner commandRunner,
            TimeProvider timeProvider,
            ILogger<McpTools> logger)
        {
            _discord = discord;
            _presence = presence;
            _gateway = gateway;
            _commands = commands;
            _commandRunner = commandRunner;
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
                int capped = limit <= 0 ? MaxRecentMessages : Math.Min(limit, MaxRecentMessages);

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

        private static string FormatMessage(DiscordChannelMessage message)
        {
            return $"[{message.Timestamp:yyyy-MM-dd HH:mm} UTC] {message.AuthorName} ({message.AuthorId}): {message.Content}";
        }

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
