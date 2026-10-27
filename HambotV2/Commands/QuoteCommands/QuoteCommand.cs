using Discord;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// Quote recall and quoting in one command: no arguments fetch a random quote
    /// of the invoking guild, a number argument fetches that exact quote, and any other argument
    /// is added as a new quote — quoting is a single, immediate action. Quote text is capped at
    /// 2000 characters (Discord's message limit), and replies trim to that limit when needed.
    /// </summary>
    internal class QuoteCommand : ICommand
    {
        /// <summary>Maximum quote text length: Discord's own message limit, so every quote fits a reply.</summary>
        internal const int MaxTextLength = 2000;

        public string Name => "quote";
        public string Description => "Show a random quote, show a quote by its number, or add new text as a quote";
        public string Usage => "quote [number|text...]";
        public int DefaultPermissionTier => PermissionTier.Everyone;
        public CommandCategory Category => CommandCategory.Quotes;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Quotes are per-guild state; the DM scope has no guild to act on.
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

            // An argument that parses as a number in long range selects that quote;
            // anything else — including numeric-looking values beyond long range — is quote text
            // to add.
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

        /// <summary>
        /// Sends a quote reply trimmed to Discord's message limit when needed: a maximal quote
        /// plus its number prefix (or a long search result list) would otherwise exceed the cap
        /// and fail delivery. The stored text is never modified — only the reply is trimmed.
        /// </summary>
        internal static async Task SendFittedAsync(IMessage message, string reply)
        {
            await message.Channel.SendMessageAsync(TrimToMessageLimit(reply));
        }

        /// <summary>Trims a reply to Discord's message limit, with a trailing ellipsis — the shared cap for quote replies and quote-of-the-day publications.</summary>
        internal static string TrimToMessageLimit(string reply)
        {
            return reply.Length > MaxTextLength
                ? reply[..(MaxTextLength - 1)] + "…"
                : reply;
        }

        /// <summary>
        /// The guild's random quote — the shared fetch behind <c>!quote</c> and the quote of the
        /// day: one count query plus one offset fetch, provider-agnostic, so the InMemory test
        /// provider exercises the same path as Npgsql. Null when the guild has no quotes.
        /// </summary>
        internal static async Task<DatabaseContext.Quote?> GetRandomQuoteAsync(IDatabaseContext database, ulong guildId)
        {
            int count = await database.Quotes.CountAsync(quote => quote.GuildId == guildId);
            if (count == 0)
                return null;

            int offset = Random.Shared.Next(count);
            return await database.Quotes
                .Where(item => item.GuildId == guildId)
                .OrderBy(item => item.Number)
                .Skip(offset)
                .FirstAsync();
        }

        private static async Task SendRandomQuote(IMessage message, ICommandContext context, ulong guildId)
        {
            var quote = await GetRandomQuoteAsync(context.DbContext, guildId);
            if (quote is null)
            {
                await message.Channel.SendMessageAsync("There are no quotes yet in this server — add one with !quote <text>");
                return;
            }

            await SendFittedAsync(message, FormatQuote(quote));
        }

        private static async Task SendQuoteByNumber(IMessage message, ICommandContext context, ulong guildId, long number)
        {
            // Quote numbers are ints, so any long above that range is simply unknown.
            var quote = number <= int.MaxValue
                ? await context.DbContext.Quotes.FirstOrDefaultAsync(item => item.GuildId == guildId && item.Number == (int)number)
                : null;

            if (quote is null)
            {
                await message.Channel.SendMessageAsync($"There is no quote with number {number} in this server");
                return;
            }

            await SendFittedAsync(message, FormatQuote(quote));
        }

        private static async Task AddQuote(IMessage message, ICommandContext context, ulong guildId, string text)
        {
            if (text.Length > MaxTextLength)
            {
                await message.Channel.SendMessageAsync($"The quote text must be at most {MaxTextLength} characters long");
                return;
            }
            // Sequential per-guild numbering, 0-based like the old bot's index list: the
            // first quote is #0, each next one greater than the guild's current maximum — so
            // removing the highest-numbered quote lets the next one reuse that top number, and
            // removal never renumbers anything.
            int nextNumber = (await context.DbContext.Quotes
                .Where(item => item.GuildId == guildId)
                .MaxAsync(item => (int?)item.Number) ?? -1) + 1;

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
