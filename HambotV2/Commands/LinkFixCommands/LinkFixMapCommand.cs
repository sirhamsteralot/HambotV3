using Discord;

using HambotV2.LinkFix;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LinkFixCommands
{
    /// <summary>
    /// The bot-owner origin→mirror table of the link reposts: the shipped defaults with the
    /// runtime overrides layered over them. Repointing an origin — or adding a site the shipped
    /// table lacks — never needs a deploy, because the mirror hosts these community-run services
    /// get rate-limited or blocked at the source. Bot-owner tier, global, DM-usable.
    /// </summary>
    internal sealed class LinkFixMapCommand : ICommand
    {
        /// <summary>The reply when the registration chain supplied no store (tests, partial hosts).</summary>
        internal const string NotConfigured = "Link reposting is not configured on this installation.";

        private const string UsageHint = "linkfixmap [<origin-host> <mirror-host> | clear <origin-host>]";

        private readonly ILinkMappingStore? _mappings;
        private readonly LinkFixSettings? _settings;

        public LinkFixMapCommand(ILinkMappingStore? mappings = null, LinkFixSettings? settings = null)
        {
            _mappings = mappings;
            _settings = settings;
        }

        public string Name => "linkfixmap";
        public string Description => "Shows or sets the link-repost origin-to-mirror mapping (global, bot owner)";
        public string Usage => UsageHint;
        public int DefaultPermissionTier => PermissionTier.BotOwner;
        public CommandCategory Category => CommandCategory.PermissionsAndSettings;

        /// <summary>Slash surface: the arguments stay free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to list the mapping, or: <origin-host> <mirror-host>, clear <origin-host>");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            if (_mappings is null)
            {
                await message.Channel.SendMessageAsync(NotConfigured);
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);
            LinkMapping mapping = await _mappings.GetAsync();

            if (arguments.Count == 0)
            {
                await message.Channel.SendMessageAsync(Describe(mapping));
                return;
            }

            if (arguments.TryGetString(0, out string first) && first == "clear")
            {
                if (!arguments.TryGetString(1, out string rawOrigin) || arguments.Count != 2)
                {
                    await message.Channel.SendMessageAsync($"Usage: {context.CommandIdentifier}{UsageHint}");
                    return;
                }

                if (!LinkHostNames.TryNormalize(rawOrigin, out string? origin))
                {
                    await message.Channel.SendMessageAsync(InvalidHostnameReply("origin", rawOrigin));
                    return;
                }

                await message.Channel.SendMessageAsync(DescribeClear(origin, await _mappings.ClearAsync(origin)));
                return;
            }

            if (arguments.Count != 2 || !arguments.TryGetString(1, out string rawMirror))
            {
                await message.Channel.SendMessageAsync($"Usage: {context.CommandIdentifier}{UsageHint}");
                return;
            }

            if (!LinkHostNames.TryNormalize(first, out string? normalizedOrigin))
            {
                await message.Channel.SendMessageAsync(InvalidHostnameReply("origin", first));
                return;
            }

            if (!LinkHostNames.TryNormalize(rawMirror, out string? mirror))
            {
                await message.Channel.SendMessageAsync(InvalidHostnameReply("mirror", rawMirror));
                return;
            }

            await message.Channel.SendMessageAsync(DescribeSet(normalizedOrigin, mirror, await _mappings.SetAsync(normalizedOrigin, mirror)));
        }

        private string Describe(LinkMapping mapping)
        {
            int maxMappings = _settings?.MaxMappings ?? LinkFixSettings.DefaultMaxMappings;
            var lines = new List<string> { $"Link fix mapping (global, {mapping.RowCount}/{maxMappings} rows):" };

            lines.AddRange(mapping.Entries.Select(entry =>
                $"{entry.OriginHost} → {entry.MirrorHost} ({LayerLabel(entry.Layer)})"));

            lines.Add($"Set one with {UsageHint}.");
            return string.Join('\n', lines);
        }

        private static string DescribeSet(string origin, string mirror, LinkMappingUpdateStatus status) => status switch
        {
            LinkMappingUpdateStatus.Applied => $"Mapped {origin} → {mirror} (runtime) — effective from the next message.",
            LinkMappingUpdateStatus.OriginIsMirrorHost =>
                $"'{origin}' is a mirror host of the current mapping — an origin may not be a mirror host, so a rewritten link is never rewritten again.",
            LinkMappingUpdateStatus.MirrorIsKnownOrigin =>
                $"'{mirror}' is an origin of the current mapping — a mirror host may not be an origin, so a rewrite never points back at the site it fixes.",
            LinkMappingUpdateStatus.CapReached =>
                "The mapping table is full — clear an origin before adding another.",
            _ => "The mapping was not changed."
        };

        private static string DescribeClear(string origin, LinkMappingUpdateStatus status)
        {
            bool hasShippedDefault = LinkMappingDefaults.TryGetEntry(origin, out LinkMappingEntry shippedDefault);

            return status switch
            {
                LinkMappingUpdateStatus.Cleared when hasShippedDefault =>
                    $"Override for {origin} cleared — the shipped default ({shippedDefault.MirrorHost}) applies again.",
                LinkMappingUpdateStatus.Cleared =>
                    $"Override for {origin} removed — that origin is no longer mapped.",
                LinkMappingUpdateStatus.NothingToClear when hasShippedDefault =>
                    $"No runtime override for {origin} — the shipped default ({shippedDefault.MirrorHost}) applies.",
                LinkMappingUpdateStatus.NothingToClear =>
                    $"No runtime override for {origin}, and the shipped table has no default for it — that origin is not mapped.",
                _ => "The mapping was not changed."
            };
        }

        private static string InvalidHostnameReply(string argument, string value) =>
            $"'{value}' is not a bare hostname for the {argument} — pass a host like fxtwitter.com, with no scheme, path, port, or userinfo.";

        private static string LayerLabel(LinkMappingLayer layer) =>
            layer == LinkMappingLayer.Runtime ? "runtime" : "shipped default";
    }
}
