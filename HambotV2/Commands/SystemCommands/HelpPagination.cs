using Discord;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// The interactive pagination of the multi-page help list: the help command attaches a
    /// previous/next button pair (each custom id encodes its owner and target page), and paging
    /// is invoker-owned — a click runs its page as a normal help invocation through the
    /// virtual-invocation seam, sharing the pipeline's permission, cooldown, and audit semantics.
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
            return PaginationButtons.GetPair(CustomId(invokerId, currentPage - 1), CustomId(invokerId, currentPage + 1), currentPage, pageCount);
        }

        /// <summary>The built previous/next button pair; null when the list fits one page.</summary>
        internal static MessageComponent? BuildButtons(ulong invokerId, int currentPage, int pageCount)
        {
            return PaginationButtons.Build(CustomId(invokerId, currentPage - 1), CustomId(invokerId, currentPage + 1), currentPage, pageCount);
        }

        /// <summary>
        /// Handles one pagination button click. A foreign clicker is declined ephemerally without
        /// running the pipeline; the invoker's click runs <c>help &lt;page&gt;</c> through the
        /// virtual-invocation seam, and only a real page reply — a multi-page page always carries
        /// its rebuilt buttons — rewrites the public message; everything else goes to the clicker
        /// ephemerally.
        /// </summary>
        internal static async Task HandleClickAsync(IPaginationButton button, IVirtualCommandRunner virtualCommandRunner)
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
