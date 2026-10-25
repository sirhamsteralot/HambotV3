namespace HambotV2.Commands.QuoteCommands
{
    internal static class RegisterQuoteCommands
    {
        public static void RegisterCommands(ICommandService commandService)
        {
            commandService.RegisterCommand("quote", new QuoteCommand());
            commandService.RegisterCommand("searchquotes", new SearchQuotesCommand());
            commandService.RegisterCommand("removequote", new RemoveQuoteCommand());
            commandService.RegisterCommand("importquotes", new ImportQuotesCommand());
        }
    }
}
