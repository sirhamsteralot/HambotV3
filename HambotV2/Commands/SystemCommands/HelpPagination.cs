using Discord;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// The button click as the pagination core sees it: free of gateway types, with the
    /// ephemeral and in-place reply paths as separate operations, so the click handling
    /// is unit-testable without a live Discord connection.
    /// </summary>
    internal interface IHelpPaginationButton
    {
        IUser User { get; }
        string CustomId { get; }
        IMessageChannel Channel { get; }
        DateTimeOffset CreatedAt { get; }
        Task DeferAsync();
        Task SendEphemeralAsync(string text);
        Task ModifyListInPlaceAsync(string content, MessageComponent components);
    }

    /// <summary>
    /// The interactive pagination of the multi-page help list: the help
    /// command attaches a previous/next button pair to a multi-page list — on text messages and
    /// interaction replies alike — and each button's custom id encodes the invoker it belongs to
    /// and the page it requests. A click is owned paging: only the invoker the list was rendered
    /// for may page it, and their click runs that page as a normal help invocation through the
    /// virtual-invocation seam, so paging shares the command pipeline's permission, cooldown,
    /// and audit semantics.
    /// </summary>
    internal static class HelpPagination
    {
        internal const string CustomIdPrefix = "help:page:";

        /// <summary>The reply to a click by anyone but the user the help list was rendered for.</summary>
        internal const string ForeignClickerReply = "Only the user who requested this help list can page through it";

        /// <summary>The custom id of the button owned by <paramref name="invokerId"/> that opens <paramref name="page"/>.</summary>
        internal static string CustomId(ulong invokerId, int page) => $"{CustomIdPrefix}{invokerId}:{page}";

        /// <summary>
        /// True when the custom id is a help pagination button; the out-parameters carry the
        /// owning invoker and the requested page.
        /// </summary>
        internal static bool TryGetTargetPage(string? customId, out ulong invokerId, out int targetPage)
        {
            invokerId = 0;
            targetPage = 0;
            if (customId is null || !customId.StartsWith(CustomIdPrefix, StringComparison.Ordinal))
                return false;

            string remainder = customId[CustomIdPrefix.Length..];
            int separator = remainder.IndexOf(':');

            // Buttons deployed before invoker ownership carry no invoker id: they parse as owned
            // by nobody (id 0), which no real user matches, so clicks on them are declined too.
            if (separator < 0)
                return int.TryParse(remainder, out targetPage) && targetPage >= 1;

            return ulong.TryParse(remainder[..separator], out invokerId)
                && int.TryParse(remainder[(separator + 1)..], out targetPage)
                && targetPage >= 1;
        }

        /// <summary>
        /// The pure button decisions around the current page — target custom id and disabled
        /// state per direction. Null when the list fits one page.
        /// </summary>
        internal static (string PreviousId, bool PreviousDisabled, string NextId, bool NextDisabled)? GetButtons(ulong invokerId, int currentPage, int pageCount)
        {
            if (pageCount <= 1)
                return null;

            return (CustomId(invokerId, currentPage - 1), currentPage <= 1, CustomId(invokerId, currentPage + 1), currentPage >= pageCount);
        }

        /// <summary>The built previous/next button pair; null when the list fits one page.</summary>
        internal static MessageComponent? BuildButtons(ulong invokerId, int currentPage, int pageCount)
        {
            var buttons = GetButtons(invokerId, currentPage, pageCount);
            if (buttons is null)
                return null;

            var (previousId, previousDisabled, nextId, nextDisabled) = buttons.Value;
            return new ComponentBuilder()
                .WithButton("◀ Previous", previousId, ButtonStyle.Secondary, disabled: previousDisabled)
                .WithButton("Next ▶", nextId, ButtonStyle.Secondary, disabled: nextDisabled)
                .Build();
        }

        /// <summary>
        /// Handles one pagination button click. The list was rendered for its invoker's visible
        /// commands, so a click by anyone else is declined ephemerally without running the
        /// pipeline — a foreign clicker must neither page the message nor have their own
        /// (shorter) list replace it. The invoker's click runs <c>help &lt;page&gt;</c> through the
        /// virtual-invocation seam; only a real page reply — a page of a multi-page list always
        /// carries its rebuilt buttons — may rewrite the public message. Declined and out-of-range
        /// invocations get their reply ephemerally and the message stays exactly as it was.
        /// </summary>
        internal static async Task HandleClickAsync(IHelpPaginationButton button, IVirtualCommandRunner virtualCommandRunner)
        {
            if (!TryGetTargetPage(button.CustomId, out ulong invokerId, out int targetPage))
                return;

            // Acknowledge within the interaction window; the reply below resolves the deferred state.
            await button.DeferAsync();

            if (button.User.Id != invokerId)
            {
                await button.SendEphemeralAsync(ForeignClickerReply);
                return;
            }

            var invocation = new VirtualCommandInvocation(button.User, button.Channel, $"help {targetPage}", button.CreatedAt);
            VirtualCommandResult result = await virtualCommandRunner.RunAsync(invocation);

            if (result.Outcome != CommandOutcome.Executed)
            {
                // The pipeline declined the paging invocation (permission, cooldown, maintenance):
                // like the slash surface, the denial is ephemeral and the paginated message stays as it is.
                foreach (VirtualCommandReply reply in result.Replies)
                    await button.SendEphemeralAsync(reply.Text);
                return;
            }

            VirtualCommandReply page = result.Replies[0];

            // A buttonless reply is the out-of-range error (or a list that no longer paginates):
            // it must never replace the public message — the clicker sees it ephemerally instead.
            if (page.Components is null)
            {
                await button.SendEphemeralAsync(page.Text);
                return;
            }

            await button.ModifyListInPlaceAsync(page.Text, page.Components);
        }
    }
}
