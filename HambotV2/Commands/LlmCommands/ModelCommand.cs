using Discord;

using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// The LLM model: no args shows the effective model and which layer supplied it,
    /// <c>list</c> shows the provider's available model ids, <c>&lt;name&gt;</c> sets the global
    /// runtime override (persisted, validated against the provider's list),
    /// <c>clear</c> removes it. Admin tier, DM-usable.
    /// </summary>
    internal sealed class ModelCommand : ICommand
    {
        private const int MaxListedModels = 25;

        private readonly ILlmGate? _gate;
        private readonly LlmSettings? _settings;

        public ModelCommand(ILlmGate? gate = null, LlmSettings? settings = null)
        {
            _gate = gate;
            _settings = settings;
        }

        public string Name => "model";
        public string Description => "Shows, lists, sets, or clears the LLM model override";
        public string Usage => "model [list|<name>|clear]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the operation stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show the effective model, or: list, <name>, clear");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_gate is null)
            {
                await message.Channel.SendMessageAsync(LlmCommandSupport.NotConfigured);
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            string? first = arguments.Get(0);

            if (first is null)
            {
                (string model, string layer) = LlmRuntimeSettings.ResolveModelWithLayer(context.DbContext, _settings ?? new LlmSettings());
                await message.Channel.SendMessageAsync($"LLM model: {model} ({layer})");
                return;
            }

            if (first == "list")
            {
                await SendModelListAsync(message);
                return;
            }

            if (first == "clear" && arguments.Count == 1)
            {
                await BotSettingOverride.ClearAsync(context.DbContext, LlmRuntimeSettings.ModelKey);
                await message.Channel.SendMessageAsync("Model override cleared — the Llm:Model configuration or the shipped default applies again.");
                return;
            }

            string? name = arguments.GetJoined(0);
            if (string.IsNullOrWhiteSpace(name))
            {
                await message.Channel.SendMessageAsync($"The model name must not be empty. Usage: {context.CommandIdentifier}{Usage}");
                return;
            }

            IReadOnlyList<string>? available = await _gate.ListModelsAsync("models");
            if (available is null)
            {
                await message.Channel.SendMessageAsync("Could not reach the model list (LLM inactive, kill switch on, budget exhausted, or API error). Nothing was changed.");
                return;
            }

            if (!available.Contains(name))
            {
                await message.Channel.SendMessageAsync($"Unknown model '{name}'. Use {context.CommandIdentifier}model list to see the available models.");
                return;
            }

            await BotSettingOverride.SetAsync(context.DbContext, LlmRuntimeSettings.ModelKey, name);
            await message.Channel.SendMessageAsync($"LLM model override set to '{name}' — applies from the next model call.");
        }

        private async Task SendModelListAsync(IMessage message)
        {
            IReadOnlyList<string>? models = await _gate!.ListModelsAsync("models");
            if (models is null)
            {
                await message.Channel.SendMessageAsync("Could not reach the model list (LLM inactive, kill switch on, budget exhausted, or API error).");
                return;
            }

            if (models.Count == 0)
            {
                await message.Channel.SendMessageAsync("The provider returned no models.");
                return;
            }

            string listed = string.Join(", ", models.Take(MaxListedModels));
            await message.Channel.SendMessageAsync(models.Count > MaxListedModels
                ? $"Available models ({models.Count}): {listed}, +{models.Count - MaxListedModels} more"
                : $"Available models ({models.Count}): {listed}");
        }
    }
}
