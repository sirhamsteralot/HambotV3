using HambotV2.Connections;
using HambotV2.Llm;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// Registers the eight LLM commands (FR-168): the bot-owner controls — <c>llmstatus</c>,
    /// <c>llmswitch</c>, <c>llmupdate</c>, <c>llminterval</c> — and the admin-tier settings —
    /// <c>model</c>, <c>persona</c>, <c>llmchat</c>, <c>llmchannels</c>. Each declares its own
    /// default tier, so the pipeline enforces them directly. All are registered even without
    /// an <c>Llm:ApiKey</c>: the gate-dependent ones then report "not configured" while
    /// <c>persona</c>/<c>llmchat</c>/<c>llmchannels</c> still work.
    /// </summary>
    internal static class RegisterLlmCommands
    {
        public static void RegisterCommands(ICommandService commandService, ILlmGate? llmGate = null, LlmSettings? llmSettings = null, ILlmUpdateService? llmUpdates = null, LlmRuntimeOptions? runtimeOptions = null, IGuildChannelLookup? guildChannels = null)
        {
            commandService.RegisterCommand("llmstatus", new LlmStatusCommand(llmGate, llmSettings, runtimeOptions));
            commandService.RegisterCommand("llmswitch", new LlmSwitchCommand(llmGate));
            commandService.RegisterCommand("llmupdate", new LlmUpdateCommand(llmUpdates));
            commandService.RegisterCommand("llminterval", new LlmIntervalCommand(runtimeOptions));
            commandService.RegisterCommand("model", new ModelCommand(llmGate, llmSettings));
            commandService.RegisterCommand("persona", new PersonaCommand(llmSettings));
            commandService.RegisterCommand("llmchat", new LlmChatCommand());
            commandService.RegisterCommand("llmchannels", new LlmChannelsCommand(guildChannels));
        }
    }
}
