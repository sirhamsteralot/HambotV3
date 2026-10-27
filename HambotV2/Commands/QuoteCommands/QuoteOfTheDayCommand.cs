using Discord;
using HambotV2.Commands.ReminderCommands;
using HambotV2.Connections;
using HambotV2.QuoteOfTheDay;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.QuoteCommands
{
    /// <summary>
    /// Manages the guild's quote-of-the-day publication at runtime: no arguments report the
    /// configuration (or that the feature is off); <c>channel</c> sets or clears the target
    /// channel — setting it activates the feature on the default schedule (daily at 00:00 UTC);
    /// <c>template</c> the message wording; and <c>&lt;due time&gt; &lt;interval&gt;</c> the
    /// schedule itself — e.g. <c>quoteoftheday 5h 1d</c> publishes the first quote in 5 hours
    /// and then every day at that UTC time. Schedule changes re-anchor to the new due time;
    /// channel and template changes leave the schedule untouched. Admin tier by default,
    /// server-scoped.
    /// </summary>
    internal sealed class QuoteOfTheDayCommand : ICommand
    {
        private readonly QuoteOfTheDaySettings _settings;

        public QuoteOfTheDayCommand(QuoteOfTheDaySettings? settings = null)
        {
            _settings = settings ?? new QuoteOfTheDaySettings();
        }

        public string Name => "quoteoftheday";
        public string Description => "Shows or configures this server's scheduled quote of the day publication";
        public string Usage => "quoteoftheday [<due time> <interval> | channel <#channel|clear> | template <text...>]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.Quotes;

        /// <summary>
        /// Slash surface: a typed channel option for the common set operation, plus the free-form
        /// line for everything else, mapping to the same text-surface tokens.
        /// </summary>
        public ApplicationCommandDeclaration? ApplicationCommand => new(
        [
            new ApplicationCommandOption("channel", "The channel to publish the quote of the day into", ApplicationCommandOptionKind.Channel, Required: false),
            ApplicationCommandOption.FreeForm("As on the text surface, e.g. '5h 1d', 'channel clear', or 'template <text>'"),
        ]);

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // The publication is per-guild state; the DM scope has no guild to configure.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server state and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (arguments.Count == 0)
            {
                await ShowAsync(message, context, guildId);
                return;
            }

            switch (arguments.Get(0))
            {
                case "channel":
                    await SetChannelAsync(message, context, guildId, arguments);
                    return;
                case "template":
                    await SetTemplateAsync(message, context, guildId, arguments);
                    return;
                default:
                    await SetScheduleAsync(message, context, guildId, arguments);
                    return;
            }
        }

        private static async Task ShowAsync(IMessage message, ICommandContext context, ulong guildId)
        {
            var setting = await context.DbContext.GuildSettings.FindAsync(guildId);

            if (setting?.QuoteOfTheDayChannelId is not ulong channelId)
            {
                await message.Channel.SendMessageAsync($"The quote of the day is off in this server — set a channel with {context.CommandIdentifier}quoteoftheday channel <#channel>");
                return;
            }

            var schedule = QuoteOfTheDaySchedule.FromSetting(setting);
            DateTimeOffset nextDue = schedule.NextBoundary(message.Timestamp);

            await message.Channel.SendMessageAsync(
                $"The quote of the day publishes to <#{channelId}> every {ReminderIntervalFormat.Format(schedule.Interval)} at {schedule.AnchorUtc:HH:mm} UTC; next due {nextDue:yyyy-MM-dd HH:mm} UTC.\n" +
                $"Template: {QuoteOfTheDaySchedule.TemplateOf(setting)}");
        }

        private static async Task SetChannelAsync(IMessage message, ICommandContext context, ulong guildId, CommandArguments arguments)
        {
            string? token = arguments.Get(1);
            ulong? channelId;
            if (token == "clear")
                channelId = null;
            else if (token is not null && TryParseChannelId(token, out ulong parsed))
                channelId = parsed;
            else
            {
                await message.Channel.SendMessageAsync("That does not look like a channel — pass a channel mention (e.g. #quotes) or a raw channel id, or clear.");
                return;
            }

            var setting = await LoadOrCreateSettingAsync(context, guildId);
            setting.QuoteOfTheDayChannelId = channelId;
            await context.DbContext.SaveChangesAsync();

            await message.Channel.SendMessageAsync(channelId is null
                ? "The quote of the day is now off in this server"
                : $"Quote of the day publications will now go to <#{channelId}> — the first one appears at the next schedule boundary");
        }

        private static async Task SetTemplateAsync(IMessage message, ICommandContext context, ulong guildId, CommandArguments arguments)
        {
            string? template = arguments.GetJoined(1);
            if (template is null)
            {
                await message.Channel.SendMessageAsync("Pass the template text after the subcommand, e.g. quoteoftheday template quote of the day: {quote}");
                return;
            }

            if (!QuoteOfTheDaySchedule.IsValidTemplate(template))
            {
                await message.Channel.SendMessageAsync($"The template must be 1 to {QuoteOfTheDaySchedule.MaxTemplateLength} characters long and contain the {{quote}} placeholder at least once");
                return;
            }

            var setting = await LoadOrCreateSettingAsync(context, guildId);
            setting.QuoteOfTheDayTemplate = template;
            await context.DbContext.SaveChangesAsync();

            await message.Channel.SendMessageAsync("The publication template was saved");
        }

        private async Task SetScheduleAsync(IMessage message, ICommandContext context, ulong guildId, CommandArguments arguments)
        {
            if (!ReminderDurationParser.TryParseFirst(arguments, 0, out TimeSpan dueIn, out int dueTokens))
            {
                await message.Channel.SendMessageAsync(
                    $"That does not look like a schedule — pass a due time and an interval, e.g. {context.CommandIdentifier}quoteoftheday 5h 1d (first quote in 5 hours, then daily at that time)");
                return;
            }

            if (!ReminderDurationParser.TryParse(arguments, dueTokens, out TimeSpan interval, out _))
            {
                await message.Channel.SendMessageAsync($"That does not look like an interval — durations like 10m, 6h, or 1d, e.g. {context.CommandIdentifier}quoteoftheday 5h 1d");
                return;
            }

            if (interval.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                await message.Channel.SendMessageAsync("The interval must be a whole number of minutes");
                return;
            }

            if (interval < _settings.MinInterval)
            {
                await message.Channel.SendMessageAsync($"The interval must be at least {ReminderIntervalFormat.Format(_settings.MinInterval)}");
                return;
            }

            if (interval > TimeSpan.FromMinutes(int.MaxValue))
            {
                await message.Channel.SendMessageAsync("The interval is too large");
                return;
            }

            var setting = await LoadOrCreateSettingAsync(context, guildId);
            setting.QuoteOfTheDayAnchorUtc = message.Timestamp + dueIn;
            setting.QuoteOfTheDayIntervalMinutes = (int)interval.TotalMinutes;
            await context.DbContext.SaveChangesAsync();

            await message.Channel.SendMessageAsync(
                $"The quote of the day will first publish at {setting.QuoteOfTheDayAnchorUtc:yyyy-MM-dd HH:mm} UTC (in {ReminderIntervalFormat.Format(dueIn)}) and then every {ReminderIntervalFormat.Format(interval)} at that time");
        }

        private static async Task<DatabaseContext.GuildSetting> LoadOrCreateSettingAsync(ICommandContext context, ulong guildId)
        {
            var setting = await context.DbContext.GuildSettings.FindAsync(guildId);
            if (setting is not null)
                return setting;

            setting = new DatabaseContext.GuildSetting { GuildId = guildId };
            context.DbContext.GuildSettings.Add(setting);
            return setting;
        }

        /// <summary>Accepts a raw channel id and a channel mention (`<#id>`).</summary>
        private static bool TryParseChannelId(string token, out ulong channelId)
        {
            channelId = 0;

            if (token.StartsWith("<#", StringComparison.Ordinal) && token.EndsWith(">", StringComparison.Ordinal))
                token = token[2..^1];

            return ulong.TryParse(token, out channelId) && channelId != 0;
        }
    }
}
