using Discord;

using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// Manages the LLM features at runtime: no args (or <c>status</c>) report configuration,
    /// kill-switch state, today's usage and budget, and consecutive failures; <c>off</c>/<c>on</c>
    /// toggle the in-memory kill switch. Bot-owner tier, DM-usable; the kill switch is not
    /// persisted — a restart restores the configured state.
    /// </summary>
    internal sealed class LlmCommand : ICommand
    {
        private readonly ILlmGate? _gate;
        private readonly LlmSettings? _settings;

        public LlmCommand(ILlmGate? gate = null, LlmSettings? settings = null)
        {
            _gate = gate;
            _settings = settings;
        }

        public string Name => "llm";
        public string Description => "Shows LLM usage status, or switches the LLM features off/on";
        public string Usage => "llm [status|off|on]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the subcommand stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit or 'status' to show usage, 'off'/'on' to kill-switch the LLM features");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_gate is null)
            {
                await message.Channel.SendMessageAsync("LLM features are not configured on this installation.");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            string? subcommand = arguments.Get(0);

            switch (subcommand)
            {
                case null:
                case "status":
                    await SendStatusAsync(message, context);
                    break;
                case "off":
                    _gate.SetKillSwitch(true);
                    await message.Channel.SendMessageAsync("LLM features are now OFF (kill switch). This is not persisted — a restart restores the configured state.");
                    break;
                case "on":
                    _gate.SetKillSwitch(false);
                    await message.Channel.SendMessageAsync("LLM features are ON again.");
                    break;
                default:
                    await message.Channel.SendMessageAsync($"Unknown subcommand '{subcommand}'. Usage: {context.CommandIdentifier}{Usage}");
                    break;
            }
        }

        private async Task SendStatusAsync(IMessage message, ICommandContext context)
        {
            LlmStateSnapshot snapshot = _gate!.Snapshot();
            (string model, string modelLayer) = LlmRuntimeSettings.ResolveModelWithLayer(context.DbContext, _settings ?? new LlmSettings());

            var lines = new List<string>
            {
                snapshot.Configured
                    ? "LLM: active"
                    : "LLM: inactive (no Llm:ApiKey configured, or the daily budget is 0)",
                $"Model: {model} ({modelLayer})",
                $"Kill switch: {(snapshot.KillSwitchActive ? "ON (all calls blocked)" : "off")}",
                $"Requests today: {snapshot.RequestsToday} / budget {snapshot.DailyBudget}{(snapshot.BudgetExhausted ? " — EXHAUSTED for the rest of the UTC day" : "")}",
                $"Consecutive failures: {snapshot.ConsecutiveFailures}"
            };

            if (snapshot.Features.Count > 0)
            {
                lines.Add("Features today: " + string.Join(", ", snapshot.Features
                    .OrderBy(entry => entry.Key)
                    .Select(entry => $"{entry.Key}: {entry.Value.Requests} requests, {entry.Value.InputTokens} tokens in / {entry.Value.OutputTokens} out")));
            }

            await message.Channel.SendMessageAsync(string.Join('\n', lines));
        }
    }
}
