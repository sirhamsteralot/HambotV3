using Discord;

namespace HambotV2.Commands
{
    /// <summary>
    /// A pagination button click as the pagination cores see it: free of gateway types, with the
    /// ephemeral and in-place reply paths as separate operations, so click handling is unit-testable
    /// without a live Discord connection.
    /// </summary>
    internal interface IPaginationButton
    {
        IUser User { get; }
        string CustomId { get; }
        IMessageChannel Channel { get; }
        DateTimeOffset CreatedAt { get; }
        Task DeferAsync();
        Task SendEphemeralAsync(string text);
        Task ModifyListInPlaceAsync(string content, MessageComponent? components);
    }

    /// <summary>
    /// The shared previous/next button pair every paginated list renders: disabled at its bounds,
    /// null when the list fits one page. Each caller composes its own custom ids; the pair logic
    /// and the button rendering are common.
    /// </summary>
    internal static class PaginationButtons
    {
        /// <summary>The disabled state per direction for the current page; null when the list fits one page.</summary>
        internal static (string PreviousId, bool PreviousDisabled, string NextId, bool NextDisabled)? GetPair(
            string previousId,
            string nextId,
            int currentPage,
            int pageCount)
        {
            if (pageCount <= 1)
                return null;

            return (previousId, currentPage <= 1, nextId, currentPage >= pageCount);
        }

        /// <summary>The built previous/next button pair; null when the list fits one page.</summary>
        internal static MessageComponent? Build(string previousId, string nextId, int currentPage, int pageCount)
        {
            var buttons = GetPair(previousId, nextId, currentPage, pageCount);
            if (buttons is null)
                return null;

            var (_, previousDisabled, _, nextDisabled) = buttons.Value;
            return new ComponentBuilder()
                .WithButton("◀ Previous", previousId, ButtonStyle.Secondary, disabled: previousDisabled)
                .WithButton("Next ▶", nextId, ButtonStyle.Secondary, disabled: nextDisabled)
                .Build();
        }
    }
}
