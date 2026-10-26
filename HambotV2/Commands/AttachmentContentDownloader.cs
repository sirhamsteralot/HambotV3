namespace HambotV2.Commands
{
    /// <summary>
    /// Downloads the text content of a message attachment (e.g. the legacy quote file of
    /// !importquotes). Abstracted so command implementations stay unit-testable with a
    /// fake instead of a live HTTP call.
    /// </summary>
    internal interface IAttachmentContentDownloader
    {
        Task<string> DownloadTextAsync(string url);
    }

    /// <summary>
    /// Production downloader over a shared HttpClient: attachment URLs are unauthenticated
    /// Discord CDN links, and reusing one client avoids port exhaustion per import.
    /// </summary>
    internal sealed class AttachmentContentDownloader : IAttachmentContentDownloader
    {
        private static readonly HttpClient Client = new();

        public async Task<string> DownloadTextAsync(string url)
        {
            return await Client.GetStringAsync(url);
        }
    }
}
