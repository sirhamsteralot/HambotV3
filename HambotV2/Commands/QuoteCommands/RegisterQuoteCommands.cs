using HambotV2.QuoteOfTheDay;

namespace HambotV2.Commands.QuoteCommands
{
    internal static class RegisterQuoteCommands
    {
        public static void RegisterCommands(ICommandService commandService, QuoteOfTheDaySettings? quoteOfTheDaySettings = null)
        {
            commandService.RegisterCommand("quote", new QuoteCommand());
            commandService.RegisterCommand("searchquotes", new SearchQuotesCommand());
            commandService.RegisterCommand("removequote", new RemoveQuoteCommand());
            commandService.RegisterCommand("importquotes", new ImportQuotesCommand());
            commandService.RegisterCommand("quoteoftheday", new QuoteOfTheDayCommand(quoteOfTheDaySettings));
        }
    }
}
