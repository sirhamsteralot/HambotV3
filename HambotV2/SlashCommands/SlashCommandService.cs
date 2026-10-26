using Discord;
using Discord.WebSocket;
using HambotV2.Commands;
using Microsoft.Extensions.Configuration;

namespace HambotV2.SlashCommands
{
    /// <summary>
    /// The slash surface (design §3.15, FR-110–FR-112): opt-in via <c>SlashCommands:Enabled</c>.
    /// After the gateway connects it pushes one application-command definition per slash-eligible
    /// command (a bulk global overwrite, idempotent on every boot), and executes invocations
    /// through the real command pipeline via the shared virtual-invocation seam — so permission
    /// tiers, cooldown keys, audit events, and error semantics are identical to the text surface.
    /// </summary>
    internal sealed class SlashCommandService
    {
        /// <summary>The generic error reply shared with the text pipeline (FR-15): no exception details.</summary>
        internal const string GenericErrorReply = "An unexpected error occurred while executing the command. Please try again later.";

        private readonly DiscordSocketClient _client;
        private readonly ICommandService _commandService;
        private readonly IVirtualCommandRunner _virtualCommandRunner;
        private readonly ILogger<SlashCommandService> _logger;
        private bool _registered;

        public SlashCommandService(DiscordSocketClient client, ICommandService commandService, IVirtualCommandRunner virtualCommandRunner, IConfiguration configuration, ILogger<SlashCommandService> logger)
        {
            _client = client;
            _commandService = commandService;
            _virtualCommandRunner = virtualCommandRunner;
            _logger = logger;
            Enabled = ParseEnabled(configuration["SlashCommands:Enabled"]);
        }

        /// <summary>False (the default) keeps the slash surface off: nothing registers, nothing executes (FR-110).</summary>
        public bool Enabled { get; }

        /// <summary>Subscribes the registration pass and the interaction handler to the shared gateway client.</summary>
        public void Attach()
        {
            _client.Ready += OnReadyAsync;
            _client.SlashCommandExecuted += OnSlashCommandExecutedAsync;
        }

        /// <summary>
        /// FR-112: the command's normal output is public, while the pipeline's denials —
        /// permission, cooldown, maintenance, and the generic error — are visible only to the invoker.
        /// </summary>
        internal static bool IsEphemeralReply(CommandOutcome outcome)
        {
            return outcome != CommandOutcome.Executed;
        }

        private async Task OnReadyAsync()
        {
            // Ready fires on every (re)connect; the registration pass runs once per boot — the
            // bulk overwrite itself is idempotent anyway.
            if (_registered)
                return;

            _registered = true;

            try
            {
                int count = await RegisterApplicationCommandsAsync();
                _logger.LogInformation("Registered {Count} application commands with Discord.", count);
            }
            catch (Exception ex)
            {
                // A failed registration disables the slash surface but never the bot: the text
                // surface keeps working (FR-112 coexistence).
                _logger.LogCritical(ex, "Registering the application commands with Discord failed; the slash surface stays unavailable this boot.");
            }
        }

        /// <summary>Builds the definitions from the registry and pushes them as one bulk global overwrite.</summary>
        internal async Task<int> RegisterApplicationCommandsAsync()
        {
            if (!_commandService.CommandsRegistered)
                _commandService.RegisterCommands();

            var properties = new List<ApplicationCommandProperties>();
            foreach (ICommand command in _commandService.Commands.Values.OrderBy(command => command.Name, StringComparer.Ordinal))
            {
                // Only the declared commands register (FR-110): administrative/configuration and
                // user-scoped commands; the rest stay text-only.
                if (command.ApplicationCommand is null)
                    continue;

                if (!ApplicationCommandDefinitionFactory.TryCreate(command, out ApplicationCommandDefinition? definition, out string? error))
                    throw new InvalidOperationException(error);

                properties.Add(BuildProperties(definition));
            }

            await ((IDiscordClient)_client).BulkOverwriteGlobalApplicationCommand([.. properties]);
            return properties.Count;
        }

        private static ApplicationCommandProperties BuildProperties(ApplicationCommandDefinition definition)
        {
            // The builder is Discord.Net's creation path for the slash payload (the properties
            // type itself has no public constructor).
            var builder = new SlashCommandBuilder()
                .WithName(definition.Name)
                .WithDescription(definition.Description);

            foreach (ApplicationCommandOption option in definition.Options)
            {
                builder.AddOption(new SlashCommandOptionBuilder
                {
                    Name = option.Name,
                    Description = option.Description,
                    Type = ToDiscordOptionType(option.Kind),
                    IsRequired = option.Required
                });
            }

            return builder.Build();
        }

        private static ApplicationCommandOptionType ToDiscordOptionType(ApplicationCommandOptionKind kind)
        {
            return kind switch
            {
                ApplicationCommandOptionKind.Text => ApplicationCommandOptionType.String,
                ApplicationCommandOptionKind.Integer => ApplicationCommandOptionType.Integer,
                ApplicationCommandOptionKind.User => ApplicationCommandOptionType.User,
                ApplicationCommandOptionKind.Channel => ApplicationCommandOptionType.Channel,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown application-command option kind."),
            };
        }

        private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
        {
            try
            {
                // Acknowledge immediately (FR-112): the pipeline may take a moment, and the
                // deferred state resolves into the followups below.
                await command.DeferAsync();

                if (!_commandService.Commands.TryGetValue(command.Data.Name, out ICommand? serviceCommand)
                    || serviceCommand.ApplicationCommand is not { } declaration)
                {
                    await command.FollowupAsync("The requested command is unavailable", ephemeral: true);
                    return;
                }

                var optionValues = command.Data.Options.ToDictionary(option => option.Name, option => (object?)option.Value);
                if (!SlashCommandLineBuilder.TryBuild(serviceCommand.Name, declaration, optionValues, out string commandLine))
                {
                    await command.FollowupAsync(GenericErrorReply, ephemeral: true);
                    return;
                }

                var invocation = new VirtualCommandInvocation(command.User, command.Channel, commandLine, command.CreatedAt);
                VirtualCommandResult result = await _virtualCommandRunner.RunAsync(invocation);

                bool ephemeral = IsEphemeralReply(result.Outcome);
                foreach (string reply in result.Replies)
                    await command.FollowupAsync(reply, ephemeral: ephemeral);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Application command '{CommandName}' invocation failed.", command.Data.Name);

                try
                {
                    if (command.HasResponded)
                        await command.FollowupAsync(GenericErrorReply, ephemeral: true);
                    else
                        await command.RespondAsync(GenericErrorReply, ephemeral: true);
                }
                catch (Exception replyEx)
                {
                    _logger.LogError(replyEx, "Failed to send the error reply for application command '{CommandName}'.", command.Data.Name);
                }
            }
        }

        /// <summary>§3.4 treatment: an absent or invalid value falls back to the default (false).</summary>
        private static bool ParseEnabled(string? configuredValue)
        {
            return bool.TryParse(configuredValue, out bool enabled) && enabled;
        }
    }
}
