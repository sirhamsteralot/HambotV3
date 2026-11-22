using HambotV2.LinkFix;

namespace HambotV2.Commands.LinkFixCommands
{
    /// <summary>
    /// Registers the two link-repost commands: the per-guild <c>linkfix</c> opt-in (admin tier)
    /// and the bot-owner <c>linkfixmap</c> mapping table. Each declares its own default tier, so
    /// the pipeline enforces them directly; <c>linkfixmap</c> needs the mapping store, which the
    /// registration chain supplies (null keeps the registry usable in tests).
    /// </summary>
    internal static class RegisterLinkFixCommands
    {
        public static void RegisterCommands(ICommandService commandService, ILinkMappingStore? mappingStore = null, LinkFixSettings? settings = null)
        {
            commandService.RegisterCommand("linkfix", new LinkFixCommand());
            commandService.RegisterCommand("linkfixmap", new LinkFixMapCommand(mappingStore, settings));
        }
    }
}
