using Discord;

using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// Manages the LLM features at runtime: no args (or <c>status</c>) report configuration,
    /// kill-switch state, today's usage and budget, and consecutive failures; <c>off</c>/<c>on</c>
    /// toggle the in-memory kill switch; <c>update [presence|bio]</c> triggers an LLM update for
    /// one surface (both when omitted) immediately, regardless of the scheduled-job flags. Run from
    /// a guild that opted in via <c>settings llmchat on</c>, the prompt includes that guild's recent
    /// chat; run from DMs, no chat is read (the scheduled jobs read every opted-in guild). Bot-owner
    /// tier, DM-usable; the kill switch is not persisted — a restart restores the configured state.
    /// </summary>
    internal sealed class LlmCommand : ICommand
    {
        private readonly ILlmGate? _gate;
        private readonly LlmSettings? _settings;
        private readonly ILlmUpdateService? _updates;

        public LlmCommand(ILlmGate? gate = null, LlmSettings? settings = null, ILlmUpdateService? updates = null)
        {
            _gate = gate;
            _settings = settings;
            _updates = updates;
        }

        public string Name => "llm";
        public string Description => "Shows LLM usage status, switches the LLM features off/on, or triggers a presence/bio update now";
        public string Usage => "llm [status|off|on|update [presence|bio]]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the subcommand stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit/'status' for status, 'off'/'on' kill-switch, 'update [presence|bio]' runs now");

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
                case "update":
                    await HandleUpdateAsync(message, context, arguments);
                    break;
                default:
                    await message.Channel.SendMessageAsync($"Unknown subcommand '{subcommand}'. Usage: {context.CommandIdentifier}{Usage}");
                    break;
            }
        }

        private async Task HandleUpdateAsync(IMessage message, ICommandContext context, CommandArguments arguments)
        {
            if (_updates is null)
            {
                await message.Channel.SendMessageAsync("LLM features are not configured on this installation.");
                return;
            }

            var surfaces = new List<LlmSurface>();
            string? which = arguments.Get(1);
            if (which is null)
            {
                surfaces.Add(LlmSurface.Presence);
                surfaces.Add(LlmSurface.Bio);
            }
            else if (which == "presence")
            {
                surfaces.Add(LlmSurface.Presence);
            }
            else if (which == "bio")
            {
                surfaces.Add(LlmSurface.Bio);
            }
            else
            {
                await message.Channel.SendMessageAsync($"Unknown surface '{which}'. Usage: {context.CommandIdentifier}{Usage}");
                return;
            }

            var lines = new List<string>();
            foreach (LlmSurface surface in surfaces)
            {
                string name = surface == LlmSurface.Presence ? "Presence" : "Bio";
                LlmUpdateOutcome outcome = await _updates.UpdateAsync(surface, context.GuildId);
                lines.Add(outcome switch
                {
                    LlmUpdateOutcome.Applied => $"{name} update applied.",
                    LlmUpdateOutcome.Inactive => "LLM features are inactive (no Llm:ApiKey configured, or the daily budget is 0) — nothing was run.",
                    LlmUpdateOutcome.Blocked => $"{name} update blocked: the kill switch is on or the daily budget is exhausted — nothing changed.",
                    LlmUpdateOutcome.Failed => $"{name} update failed (logged; counts toward the consecutive-failure watchdog alert) — nothing changed.",
                    LlmUpdateOutcome.InvalidOutput => $"{name} update produced invalid output — the current {name.ToLowerInvariant()} was kept.",
                    _ => $"{name} update returned an unknown outcome."
                });
            }

            await message.Channel.SendMessageAsync(string.Join('\n', lines));
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
