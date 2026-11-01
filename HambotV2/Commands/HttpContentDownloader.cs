using System.Text;

namespace HambotV2.Commands
{
    /// <summary>
    /// The one seam for downloading URL content (attachment CDN links, user-supplied image
    /// URLs): text or bytes over a single shared HttpClient, each response capped at a
    /// caller-supplied byte limit so a huge response fails the download instead of exhausting
    /// memory. Content validation is a caller-side post-step where the kind needs it (image
    /// sniff + size/type limits for profile images, none for the quote import). Abstracted so
    /// the commands stay unit-testable with a fake instead of a live HTTP call.
    /// </summary>
    internal interface IHttpContentDownloader
    {
        Task<string> DownloadTextAsync(string url, long maxBytes, CancellationToken cancellationToken = default);

        Task<byte[]> DownloadBytesAsync(string url, long maxBytes, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Production downloader over one shared HttpClient — reusing one client avoids port
    /// exhaustion per download. Non-success statuses and cap violations throw
    /// <see cref="HttpRequestException"/>; a timeout throws <see cref="TaskCanceledException"/>.
    /// </summary>
    internal sealed class HttpContentDownloader : IHttpContentDownloader
    {
        /// <summary>Cap for text content (the legacy quoteSettings.json import): generous, but bounded.</summary>
        internal const long MaxTextBytes = 16L * 1024 * 1024;

        private static readonly HttpClient Client = new();

        public async Task<string> DownloadTextAsync(string url, long maxBytes, CancellationToken cancellationToken = default)
        {
            byte[] data = await DownloadBytesAsync(url, maxBytes, cancellationToken);
            return Encoding.UTF8.GetString(data);
        }

        public async Task<byte[]> DownloadBytesAsync(string url, long maxBytes, CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // The declared length is checked up front; the stream read below enforces the cap
            // even when the server does not declare one.
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                throw new HttpRequestException($"The response exceeds the {maxBytes}-byte download cap (declared {declared} bytes).");

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new HttpRequestException($"The response exceeds the {maxBytes}-byte download cap.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            return buffer.ToArray();
        }
    }
}
