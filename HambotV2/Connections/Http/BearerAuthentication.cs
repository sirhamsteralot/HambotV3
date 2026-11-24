using System.Security.Cryptography;
using System.Text;

namespace HambotV2.Connections.Http
{
    /// <summary>
    /// The bearer-token check shared by every inbound HTTP surface: the request must carry
    /// <c>Bearer &lt;token&gt;</c>; anything else is rejected before any work runs. The comparison is
    /// constant-time so the token is not exposed through timing.
    /// </summary>
    internal static class BearerAuthentication
    {
        public const string Scheme = "Bearer";

        public static bool IsValid(string? authorizationHeader, string token)
        {
            if (authorizationHeader is null || token.Length == 0)
                return false;

            ReadOnlySpan<byte> expected = Encoding.UTF8.GetBytes($"{Scheme} {token}");
            ReadOnlySpan<byte> actual = Encoding.UTF8.GetBytes(authorizationHeader);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }
}
