using Discord;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// The interactive pagination of the multi-page help list (design §3.2/§3.15): the help
    /// command attaches a previous/next button pair to a multi-page list — on text messages and
    /// interaction replies alike — and each button's custom id encodes the page it requests.
    /// The button handler runs that page as a normal help invocation through the virtual-invocation
    /// seam, so paging shares the command pipeline's permission, cooldown, and audit semantics.
    /// </summary>
    internal static class HelpPagination
    {
        internal const string CustomIdPrefix = "help:page:";

        /// <summary>The custom id of the button that opens <paramref name="page"/>.</summary>
        internal static string CustomId(int page) => $"{CustomIdPrefix}{page}";

        /// <summary>True when the custom id is a help pagination button; the out-parameter carries the requested page.</summary>
        internal static bool TryGetTargetPage(string? customId, out int targetPage)
        {
            targetPage = 0;
            return customId is not null
                && customId.StartsWith(CustomIdPrefix, StringComparison.Ordinal)
                && int.TryParse(customId.AsSpan(CustomIdPrefix.Length), out targetPage)
                && targetPage >= 1;
        }

        /// <summary>
        /// The pure button decisions around the current page — target custom id and disabled
        /// state per direction. Null when the list fits one page.
        /// </summary>
        internal static (string PreviousId, bool PreviousDisabled, string NextId, bool NextDisabled)? GetButtons(int currentPage, int pageCount)
        {
            if (pageCount <= 1)
                return null;

            return (CustomId(currentPage - 1), currentPage <= 1, CustomId(currentPage + 1), currentPage >= pageCount);
        }

        /// <summary>The built previous/next button pair; null when the list fits one page.</summary>
        internal static MessageComponent? BuildButtons(int currentPage, int pageCount)
        {
            var buttons = GetButtons(currentPage, pageCount);
            if (buttons is null)
                return null;

            var (previousId, previousDisabled, nextId, nextDisabled) = buttons.Value;
            return new ComponentBuilder()
                .WithButton("◀ Previous", previousId, ButtonStyle.Secondary, disabled: previousDisabled)
                .WithButton("Next ▶", nextId, ButtonStyle.Secondary, disabled: nextDisabled)
                .Build();
        }
    }
}
