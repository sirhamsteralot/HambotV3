using HambotV2.Llm;

namespace HambotV2.Commands.LlmCommands
{
    internal static class RegisterLlmCommands
    {
        public static void RegisterCommands(ICommandService commandService, ILlmGate? llmGate = null, LlmSettings? llmSettings = null)
        {
            commandService.RegisterCommand("llm", new LlmCommand(llmGate, llmSettings));
            commandService.RegisterCommand("persona", new PersonaCommand(llmSettings));
            commandService.RegisterCommand("model", new ModelCommand(llmGate, llmSettings));
        }
    }
}
