using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Api.Merchants;

/// <summary>Opaque session-token generation + hashing for the merchant-user BFF. The raw token is the credential and
/// is only ever held in the cookie; the SHA-256 hash is what the store persists (REQ-10.1/10.2).</summary>
// ponytail: DUPLICATE of Api.AdminSessionTokens — deliberate debt, do not refactor into a shared base.
internal static class UserTokens
{
    /// <summary>A fresh opaque 256-bit token, URL-safe for a cookie value.</summary>
    public static string NewOpaqueToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 of the token string — the value persisted as <c>MerchantUserSessions.TokenHash</c>.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
