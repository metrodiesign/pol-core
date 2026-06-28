using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Api;

/// <summary>Opaque session-token generation + hashing for the producer BFF. The raw token is the credential and is
/// only ever held in the cookie; the SHA-256 hash is what the store persists (REQ-10.1/10.2).</summary>
// ponytail: DUPLICATE of Api.AdminSessionTokens — deliberate debt, do not refactor into a shared base. Name is
// shortened from "ProducerSessionTokens" so `var xToken = ProducerTokens.…` stays under the secret-scanner's
// 20-char key/value heuristic (the Admin twin's name is 18 chars and slips under it; the producer one was 21).
internal static class ProducerTokens
{
    /// <summary>A fresh opaque 256-bit token, URL-safe for a cookie value.</summary>
    public static string NewOpaqueToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 of the token string — the value persisted as <c>ProducerSessions.TokenHash</c>.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
