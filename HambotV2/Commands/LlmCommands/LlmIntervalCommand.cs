using Discord;

using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// Runtime interval override for one update job: no value shows the current interval,
    /// a whole number of minutes (floor 1) sets it, <c>reset</c> restores the configured value.
    /// In-memory — a restart restores the configured state. Bot-owner tier, DM-usable.
    /// </summary>
    internal sealed class LlmIntervalCommand : ICommand
    {
        private readonly LlmRuntimeOptions? _runtimeOptions;

        public LlmIntervalCommand(LlmRuntimeOptions? runtimeOptions = null)
        {
            _runtimeOptions = runtimeOptions;
        }

        public string Name => "llminterval";
        public string Description => "Shows or sets an LLM update job's runtime interval in minutes";
        public string Usage => "llminterval <presence|bio> [minutes|reset]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the arguments stay free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("e.g. 'presence 5', 'bio reset', or 'presence' to show the current interval");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_runtimeOptions is null)
            {
                await message.Channel.SendMessageAsync(LlmCommandSupport.NotConfigured);
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (!LlmCommandSupport.TryParseSurface(arguments.Get(0), out LlmSurface surface))
            {
                await message.Channel.SendMessageAsync($"Usage: {context.CommandIdentifier}{Usage}");
                return;
            }

            string label = LlmCommandSupport.SurfaceLabel(surface);

            if (arguments.Count == 1)
            {
                int current = _runtimeOptions.GetInterval(surface);
                await message.Channel.SendMessageAsync($"{label} job interval: {current} min" +
                    (_runtimeOptions.IsRuntimeOverride(surface) ? " (runtime override)" : " (configured)"));
                return;
            }

            if (arguments.Get(1) == "reset")
            {
                _runtimeOptions.ResetInterval(surface);
                int configured = _runtimeOptions.GetInterval(surface);
                await message.Channel.SendMessageAsync($"{label} job interval reset to the configured {configured} min — applies from the next tick (not persisted; a restart restores the configured value).");
                return;
            }

            if (!arguments.TryGetInt(1, out int minutes) || minutes < 1)
            {
                await message.Channel.SendMessageAsync("The interval must be a whole number of minutes of at least 1, or 'reset'.");
                return;
            }

            _runtimeOptions.SetInterval(surface, minutes);
            await message.Channel.SendMessageAsync($"{label} job interval set to {minutes} min — applies from the next tick (not persisted; a restart restores the configured value).");
        }
    }
}
