namespace HambotV2.Commands
{
    /// <summary>
    /// Downloads the raw bytes of a profile image — an attachment's CDN URL or a
    /// user-supplied image URL. Abstracted like <see cref="IAttachmentContentDownloader"/> so the
    /// avatar/banner commands are unit-testable with a fake.
    /// </summary>
    internal interface IImageContentDownloader
    {
        Task<byte[]> DownloadBytesAsync(string url);
    }

    /// <summary>Production downloader over a shared HttpClient, avoiding port exhaustion per image.</summary>
    internal sealed class ImageContentDownloader : IImageContentDownloader
    {
        private static readonly HttpClient Client = new();

        public async Task<byte[]> DownloadBytesAsync(string url)
        {
            return await Client.GetByteArrayAsync(url);
        }
    }
}
