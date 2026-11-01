using Discord;

using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// Immediately runs an LLM update for one surface (presence or bio) — both when the
    /// argument is omitted — regardless of the scheduled-job flags, but still through the
    /// gate (kill switch, daily budget, accounting). Bot-owner tier, DM-usable.
    /// </summary>
    internal sealed class LlmUpdateCommand : ICommand
    {
        private readonly ILlmUpdateService? _updates;

        public LlmUpdateCommand(ILlmUpdateService? updates = null)
        {
            _updates = updates;
        }

        public string Name => "llmupdate";
        public string Description => "Runs an LLM presence/bio update now, even with the scheduled jobs off";
        public string Usage => "llmupdate [presence|bio]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the surface stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit for both surfaces, or: presence, bio");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_updates is null)
            {
                await message.Channel.SendMessageAsync(LlmCommandSupport.NotConfigured);
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            var surfaces = new List<LlmSurface>();
            string? which = arguments.Get(0);
            if (which is null)
            {
                surfaces.Add(LlmSurface.Presence);
                surfaces.Add(LlmSurface.Bio);
            }
            else if (LlmCommandSupport.TryParseSurface(which, out LlmSurface surface))
            {
                surfaces.Add(surface);
            }
            else
            {
                await message.Channel.SendMessageAsync($"Unknown surface '{which}'. Usage: {context.CommandIdentifier}{Usage}");
                return;
            }

            var lines = new List<string>();
            foreach (LlmSurface surface in surfaces)
            {
                string name = LlmCommandSupport.SurfaceLabel(surface);
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
    }
}
