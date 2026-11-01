using Discord;

using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// Reports the LLM state: active/inactive, the effective model and its layer, the kill
    /// switch, presence/bio job states with their intervals, per-feature usage for the current
    /// UTC day, the daily budget, and consecutive failures. Bot-owner tier, DM-usable.
    /// </summary>
    internal sealed class LlmStatusCommand : ICommand
    {
        private readonly ILlmGate? _gate;
        private readonly LlmSettings? _settings;
        private readonly LlmRuntimeOptions? _runtimeOptions;

        public LlmStatusCommand(ILlmGate? gate = null, LlmSettings? settings = null, LlmRuntimeOptions? runtimeOptions = null)
        {
            _gate = gate;
            _settings = settings;
            _runtimeOptions = runtimeOptions;
        }

        public string Name => "llmstatus";
        public string Description => "Reports the LLM state: model, kill switch, job intervals, usage, budget, failures";
        public string Usage => "llmstatus";
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: administrative, no arguments.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand => ApplicationCommandDeclaration.NoArguments;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_gate is null)
            {
                await message.Channel.SendMessageAsync(LlmCommandSupport.NotConfigured);
                return;
            }

            LlmStateSnapshot snapshot = _gate.Snapshot();
            var settings = _settings ?? new LlmSettings();
            var options = _runtimeOptions ?? new LlmRuntimeOptions(settings);
            (string model, string modelLayer) = LlmRuntimeSettings.ResolveModelWithLayer(context.DbContext, settings);

            var lines = new List<string>
            {
                snapshot.Configured
                    ? "LLM: active"
                    : "LLM: inactive (no Llm:ApiKey configured, or the daily budget is 0)",
                $"Model: {model} ({modelLayer})",
                $"Kill switch: {(snapshot.KillSwitchActive ? "ON (all calls blocked)" : "off")}",
                $"Presence job: {(settings.PresenceUpdatesEnabled ? $"on (every {options.GetInterval(LlmSurface.Presence)} min{FormatOverride(options, LlmSurface.Presence)})" : "off")}",
                $"Bio job: {(settings.BioUpdatesEnabled ? $"on (every {options.GetInterval(LlmSurface.Bio)} min{FormatOverride(options, LlmSurface.Bio)})" : "off")}",
                $"Requests today: {snapshot.RequestsToday} / budget {snapshot.DailyBudget}{(snapshot.BudgetExhausted ? " — EXHAUSTED for the rest of the UTC day" : "")}",
                $"Consecutive failures: {snapshot.ConsecutiveFailures}"
            };

            if (snapshot.Features.Count > 0)
            {
                lines.Add("Features today: " + string.Join(", ", snapshot.Features
                    .OrderBy(entry => entry.Key)
                    .Select(entry => $"{entry.Key}: {entry.Value.Requests} requests, {entry.Value.InputTokens} tokens in / {entry.Value.OutputTokens} out")));
            }

            lines.Add("Settings: model | persona [global|server] | llmchat | llmchannels (admin tier)");

            await message.Channel.SendMessageAsync(string.Join('\n', lines));
        }

        private static string FormatOverride(LlmRuntimeOptions options, LlmSurface surface) =>
            options.IsRuntimeOverride(surface) ? ", runtime" : "";
    }
}
