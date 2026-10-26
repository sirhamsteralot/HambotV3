using System.Security.Cryptography;
using System.Text;

namespace HambotV2.Mcp
{
    /// <summary>
    /// The bearer-key check in front of the MCP endpoint: every request must carry
    /// the configured <c>Mcp:ApiKey</c>; anything else is rejected before any tool logic runs.
    /// The comparison is constant-time so the key is not exposed through timing.
    /// </summary>
    internal static class McpBearerAuthentication
    {
        public const string Scheme = "Bearer";

        public static bool IsValid(string? authorizationHeader, string apiKey)
        {
            if (authorizationHeader is null)
                return false;

            ReadOnlySpan<byte> expected = Encoding.UTF8.GetBytes($"{Scheme} {apiKey}");
            ReadOnlySpan<byte> actual = Encoding.UTF8.GetBytes(authorizationHeader);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }
}
