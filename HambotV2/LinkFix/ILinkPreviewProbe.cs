using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace HambotV2.LinkFix
{
    /// <summary>What a mirror's unfurl metadata says about a link — the Reddit video gate's three outcomes.</summary>
    internal enum PreviewVerdict
    {
        /// <summary>The metadata declares <c>og:video</c>: the repost buys a video embed.</summary>
        Video,

        /// <summary>The metadata is there and declares no video: a text or image post.</summary>
        NoVideo,

        /// <summary>No metadata at all, or a truncated read — a check that proved nothing.</summary>
        NoMetadata
    }

    /// <summary>The preview seam of the Reddit video gate, so the repost path is testable without HTTP.</summary>
    internal interface ILinkPreviewProbe
    {
        /// <summary>Reads one mirror URL's unfurl metadata; a failed request throws.</summary>
        Task<PreviewVerdict> ReadVerdictAsync(string url);
    }

    /// <summary>
    /// Production probe over one shared <see cref="HttpClient"/>: the mirrors serve unfurl metadata only to
    /// Discord's crawler, so the request carries a Discordbot-compatible user agent.
    /// </summary>
    internal sealed class HttpLinkPreviewProbe : ILinkPreviewProbe
    {
        /// <summary>The read cap: a preview page is a few kilobytes, so a longer one is not a preview page.</summary>
        internal const int MaxProbeBytes = 128 * 1024;

        internal const string UserAgent = "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com) HambotV2-link-preview/1.0";

        /// <summary>The declaration, quoted or not, in any of its forms (<c>og:video</c>, <c>og:video:url</c>, …).</summary>
        private static readonly Regex VideoMetaTag = new(
            "property\\s*=\\s*[\"']?og:video(?=[\"':>\\s]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Any OpenGraph declaration — what tells "no video" apart from "no metadata".</summary>
        private static readonly Regex OpenGraphMetaTag = new(
            "property\\s*=\\s*[\"']?og:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly LinkFixSettings _settings;
        private readonly HttpClient _client;

        public HttpLinkPreviewProbe(LinkFixSettings settings, HttpClient? client = null)
        {
            _settings = settings;
            _client = client ?? SharedClient;
        }

        public async Task<PreviewVerdict> ReadVerdictAsync(string url)
        {
            using var timeout = new CancellationTokenSource(_settings.VideoProbeTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxProbeBytes);
            try
            {
                int read = 0;
                while (read < MaxProbeBytes)
                {
                    int chunk = await stream.ReadAsync(buffer.AsMemory(read, MaxProbeBytes - read), timeout.Token);
                    if (chunk == 0)
                        break;

                    read += chunk;
                }

                return Classify(Encoding.UTF8.GetString(buffer, 0, read), truncated: read >= MaxProbeBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>The verdict a page declares; a truncated read proves nothing beyond a video it still declares.</summary>
        internal static PreviewVerdict Classify(string html, bool truncated)
        {
            if (VideoMetaTag.IsMatch(html))
                return PreviewVerdict.Video;

            return !truncated && OpenGraphMetaTag.IsMatch(html) ? PreviewVerdict.NoVideo : PreviewVerdict.NoMetadata;
        }
    }
}
