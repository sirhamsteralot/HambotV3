using Discord;

using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// The in-memory LLM kill switch: no args shows the state, <c>off</c> blocks every LLM
    /// call, <c>on</c> clears the block. A restart restores the configured state.
    /// Bot-owner tier, DM-usable.
    /// </summary>
    internal sealed class LlmSwitchCommand : ICommand
    {
        private readonly ILlmGate? _gate;

        public LlmSwitchCommand(ILlmGate? gate = null)
        {
            _gate = gate;
        }

        public string Name => "llmswitch";
        public string Description => "Shows or sets the LLM kill switch (off blocks every LLM call)";
        public string Usage => "llmswitch [on|off]";
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the operation stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show the kill-switch state, or: on, off");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_gate is null)
            {
                await message.Channel.SendMessageAsync(LlmCommandSupport.NotConfigured);
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            switch (arguments.Get(0))
            {
                case null:
                    await message.Channel.SendMessageAsync($"Kill switch: {(_gate.KillSwitchActive ? "ON (all calls blocked)" : "off")}");
                    return;
                case "on":
                    _gate.SetKillSwitch(false);
                    await message.Channel.SendMessageAsync("LLM features are ON again.");
                    return;
                case "off":
                    _gate.SetKillSwitch(true);
                    await message.Channel.SendMessageAsync("LLM features are now OFF (kill switch). This is not persisted — a restart restores the configured state.");
                    return;
                default:
                    await message.Channel.SendMessageAsync($"Usage: {context.CommandIdentifier}{Usage}");
                    return;
            }
        }
    }
}
