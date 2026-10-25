using Discord;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// Quote recall and quoting in one command (FR-44–FR-47): no arguments fetch a random quote
    /// of the invoking guild, a number argument fetches that exact quote, and any other argument
    /// is added as a new quote — quoting is a single, immediate action.
    /// </summary>
    internal class QuoteCommand : ICommand
    {
        /// <summary>Maximum quote text length (FR-47).</summary>
        internal const int MaxTextLength = 500;

        public string Name => "quote";
        public string Description => "Show a random quote, show a quote by its number, or add new text as a quote";
        public string Usage => "quote [number|text...]";
        public int DefaultPermissionTier => PermissionTier.Everyone;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Quotes are per-guild state; the DM scope (FR-25/FR-50) has no guild to act on.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (arguments.Count == 0)
            {
                await SendRandomQuote(message, context, guildId);
                return;
            }

            // An argument that parses as a number in long range selects that quote (FR-45);
            // anything else — including numeric-looking values beyond long range — is quote text
            // to add (FR-47).
            if (long.TryParse(arguments.Get(0), out long number))
            {
                await SendQuoteByNumber(message, context, guildId, number);
                return;
            }

            await AddQuote(message, context, guildId, arguments.GetJoined(0)!);
        }

        /// <summary>Number-prefixed single-line quote format shared by the quote commands.</summary>
        internal static string FormatQuote(DatabaseContext.Quote quote)
        {
            return $"#{quote.Number}: {quote.Text}";
        }

        private static async Task SendRandomQuote(IMessage message, ICommandContext context, ulong guildId)
        {
            // One count query plus one offset fetch: provider-agnostic, so the InMemory test
            // provider exercises the same path as Npgsql.
            int count = await context.DbContext.Quotes.CountAsync(quote => quote.GuildId == guildId);
            if (count == 0)
            {
                await message.Channel.SendMessageAsync("There are no quotes yet in this server — add one with !quote <text>");
                return;
            }

            int offset = Random.Shared.Next(count);
            var quote = await context.DbContext.Quotes
                .Where(item => item.GuildId == guildId)
                .OrderBy(item => item.Number)
                .Skip(offset)
                .FirstAsync();

            await message.Channel.SendMessageAsync(FormatQuote(quote));
        }

        private static async Task SendQuoteByNumber(IMessage message, ICommandContext context, ulong guildId, long number)
        {
            // Quote numbers are ints, so any long above that range is simply unknown (FR-45).
            var quote = number <= int.MaxValue
                ? await context.DbContext.Quotes.FirstOrDefaultAsync(item => item.GuildId == guildId && item.Number == (int)number)
                : null;

            if (quote is null)
            {
                await message.Channel.SendMessageAsync($"There is no quote with number {number} in this server");
                return;
            }

            await message.Channel.SendMessageAsync(FormatQuote(quote));
        }

        private static async Task AddQuote(IMessage message, ICommandContext context, ulong guildId, string text)
        {
            if (text.Length > MaxTextLength)
            {
                await message.Channel.SendMessageAsync($"The quote text must be at most {MaxTextLength} characters long");
                return;
            }

            // Sequential per-guild numbering (FR-43): always one greater than the current
            // maximum — so removing the highest-numbered quote lets the next one reuse that top
            // number, and removal never renumbers anything.
            int nextNumber = (await context.DbContext.Quotes
                .Where(item => item.GuildId == guildId)
                .MaxAsync(item => (int?)item.Number) ?? 0) + 1;

            context.DbContext.Quotes.Add(new DatabaseContext.Quote
            {
                GuildId = guildId,
                Number = nextNumber,
                Text = text,
                AddedByDiscordId = message.Author.Id,
                CreatedAt = message.Timestamp.UtcDateTime
            });
            await context.DbContext.SaveChangesAsync();

            await message.Channel.SendMessageAsync($"Added quote #{nextNumber}");
        }
    }
}
