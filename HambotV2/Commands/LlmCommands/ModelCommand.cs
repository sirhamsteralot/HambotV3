using Discord;

using HambotV2.Connections;
using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// Manages the global LLM model: no args show the effective model and which layer supplied it,
    /// <c>list</c> lists the provider's available models, <c>&lt;name&gt;</c> sets the runtime
    /// override (persisted in global storage, validated against the provider's model list),
    /// <c>clear</c> removes it so <c>Llm:Model</c> or the shipped default applies. Admin tier,
    /// DM-usable. The override applies to every model call from the next one — no restart.
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
        public string Description => "Shows, lists, sets, or clears the LLM model (runtime override over Llm:Model)";
        public string Usage => "model [list | <name> | clear]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the argument stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show, 'list' for available models, <name> to set, clear to reset");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_gate is null)
            {
                await message.Channel.SendMessageAsync("LLM features are not configured on this installation.");
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
                var existing = context.DbContext.BotSettings.FirstOrDefault(setting => setting.Key == LlmRuntimeSettings.ModelKey);
                if (existing is not null)
                    context.DbContext.BotSettings.Remove(existing);
                await context.DbContext.SaveChangesAsync();
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

            var setting = context.DbContext.BotSettings.FirstOrDefault(item => item.Key == LlmRuntimeSettings.ModelKey);
            if (setting is null)
            {
                setting = new DatabaseContext.BotSetting { Key = LlmRuntimeSettings.ModelKey };
                context.DbContext.BotSettings.Add(setting);
            }

            setting.Value = name;
            await context.DbContext.SaveChangesAsync();
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
