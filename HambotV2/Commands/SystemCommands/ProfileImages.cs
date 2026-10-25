using System.Diagnostics.CodeAnalysis;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Profile image validation (FR-115/FR-116): Discord accepts PNG, JPEG, and GIF up to 10 MB.
    /// Content is verified by signature, not by the client-declared content type.
    /// </summary>
    internal static class ProfileImages
    {
        public const int MaxImageBytes = 10 * 1024 * 1024;

        /// <summary>True for a supported image within the size limit; otherwise false with an error fragment for the in-channel reply.</summary>
        public static bool TryValidate(byte[] data, [NotNullWhen(false)] out string? error)
        {
            if (data.Length > MaxImageBytes)
            {
                error = $"the image is larger than Discord's limit of 10 MB ({data.Length:N0} bytes)";
                return false;
            }

            if (IsPng(data) || IsJpeg(data) || IsGif(data))
            {
                error = null;
                return true;
            }

            error = "the data is not a PNG, JPEG, or GIF image";
            return false;
        }

        private static bool IsPng(byte[] data)
        {
            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            return data.AsSpan().StartsWith(signature);
        }

        private static bool IsJpeg(byte[] data)
        {
            ReadOnlySpan<byte> signature = [0xFF, 0xD8, 0xFF];
            return data.AsSpan().StartsWith(signature);
        }

        private static bool IsGif(byte[] data)
        {
            return data.AsSpan().StartsWith("GIF87a"u8) || data.AsSpan().StartsWith("GIF89a"u8);
        }
    }
}
