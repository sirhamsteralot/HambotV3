using HambotV2.QuoteOfTheDay;

namespace HambotV2.Commands.QuoteCommands
{
    internal static class RegisterQuoteCommands
    {
        public static void RegisterCommands(ICommandService commandService, QuoteSearchBacklog quoteSearchBacklog, QuoteOfTheDaySettings? quoteOfTheDaySettings = null)
        {
            commandService.RegisterCommand("quote", new QuoteCommand());
            commandService.RegisterCommand("searchquotes", new SearchQuotesCommand(quoteSearchBacklog));
            commandService.RegisterCommand("removequote", new RemoveQuoteCommand());
            commandService.RegisterCommand("importquotes", new ImportQuotesCommand());
            commandService.RegisterCommand("quoteoftheday", new QuoteOfTheDayCommand(quoteOfTheDaySettings));
        }
    }
}
