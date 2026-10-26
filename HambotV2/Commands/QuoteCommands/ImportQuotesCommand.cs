using Discord;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// Imports the old bot's quotes into the invoking guild: the configured bot
    /// owner attaches the old `quoteSettings.json` to the invocation message. Tier 4 by default —
    /// reachable only through configuration and effectively immutable at runtime — so
    /// only the bot owner can ever run it.
    /// </summary>
    internal class ImportQuotesCommand : ICommand
    {
        private readonly IAttachmentContentDownloader _downloader;

        public ImportQuotesCommand(IAttachmentContentDownloader? downloader = null)
        {
            _downloader = downloader ?? new AttachmentContentDownloader();
        }

        public string Name => "importquotes";
        public string Description => "Import the old bot's quotes into this server by attaching its quoteSettings.json file to the message";
        public string Usage => "importquotes";
        public int DefaultPermissionTier => PermissionTier.BotOwner;

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Quotes are per-guild state; the DM scope has no guild to import into.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            if (message.Attachments.FirstOrDefault() is not IAttachment attachment)
            {
                await message.Channel.SendMessageAsync("Attach the old bot's quoteSettings.json file to the message to import its quotes");
                return;
            }

            string content;
            try
            {
                content = await _downloader.DownloadTextAsync(attachment.Url);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await message.Channel.SendMessageAsync("The attached file could not be downloaded — please reattach it and try again");
                return;
            }

            List<LegacyQuote>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<LegacyQuoteFile>(content)?.quotes;
            }
            catch (JsonException)
            {
                await message.Channel.SendMessageAsync("The attached file is not valid quoteSettings.json — its content could not be parsed");
                return;
            }

            if (entries is null || entries.Count == 0)
            {
                await message.Channel.SendMessageAsync("The attached file contains no quotes to import");
                return;
            }

            // All-or-nothing validation: the whole file is checked before anything is
            // stored, so a failed import leaves the guild's quotes untouched.
            for (int index = 0; index < entries.Count; index++)
            {
                string? text = entries[index].quote;
                if (string.IsNullOrWhiteSpace(text))
                {
                    await message.Channel.SendMessageAsync($"Quote {index + 1} in the file is empty — nothing was imported");
                    return;
                }

                if (text.Length > QuoteCommand.MaxTextLength)
                {
                    await message.Channel.SendMessageAsync($"Quote {index + 1} in the file is longer than {QuoteCommand.MaxTextLength} characters — nothing was imported");
                    return;
                }
            }

            // Storage: appended in file order, continuing the per-guild numbering,
            // 0-based like the old bot's index list — an empty guild imports starting at #0 — the
            // legacy date survives the migration, falling back to the import message.
            int nextNumber = (await context.DbContext.Quotes
                .Where(item => item.GuildId == guildId)
                .MaxAsync(item => (int?)item.Number) ?? -1) + 1;
            int firstNumber = nextNumber;

            foreach (var entry in entries)
            {
                context.DbContext.Quotes.Add(new DatabaseContext.Quote
                {
                    GuildId = guildId,
                    Number = nextNumber++,
                    Text = entry.quote!,
                    AddedByDiscordId = message.Author.Id,
                    CreatedAt = entry.date?.UtcDateTime ?? message.Timestamp.UtcDateTime
                });
            }

            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Imported {entries.Count} quotes, assigned numbers #{firstNumber} to #{nextNumber - 1}");
        }

        /// <summary>The old bot's file shape: a `quotes` array of `{ quote, date }` entries (date optional).</summary>
        private sealed class LegacyQuoteFile
        {
            public List<LegacyQuote>? quotes { get; set; }
        }

        private sealed class LegacyQuote
        {
            public string? quote { get; set; }
            public DateTimeOffset? date { get; set; }
        }
    }
}
