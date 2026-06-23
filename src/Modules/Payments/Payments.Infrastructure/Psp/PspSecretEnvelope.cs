using System.Text.Json.Serialization;

namespace Payments.Infrastructure.Psp;

// The single plaintext returned by IVaultSecretStore.RevealAsync for a PSP connection is a small
// camelCase JSON envelope, NOT a raw key. A PSP charge needs more than one value (2C2P needs a
// merchant id alongside its secret key; Omise PromptPay needs the Payment Links+ template/team ids),
// but the seam reveals exactly one string and PspConnection must not grow secret-bearing columns. So
// the structured bundle rides inside that one revealed string and is parsed here, invisible above the
// adapter boundary. Every adapter (and any test double) MUST agree on this shape — see IVaultSecretStore.

/// <summary>The 2C2P credential bundle stored as the vault plaintext. merchantID is an identifier, not a
/// secret, but it co-locates here to keep the seam a single reveal.</summary>
internal sealed record TwoCTwoPSecret(
    [property: JsonPropertyName("merchantId")] string MerchantId,
    [property: JsonPropertyName("secretKey")] string SecretKey);

/// <summary>The Omise credential bundle stored as the vault plaintext. The secret key's prefix
/// (skey_test_ / skey_live_) selects test vs live. <c>PublicKey</c> / <c>WebhookSecret</c> are stored
/// as provided at provisioning (reference 2.4) and are optional — the redirect-card path uses only
/// the secret key today; the webhook secret backs deferred HMAC verification.</summary>
internal sealed record OmiseSecret(
    [property: JsonPropertyName("secretKey")] string SecretKey,
    [property: JsonPropertyName("publicKey")] string? PublicKey = null,
    [property: JsonPropertyName("webhookSecret")] string? WebhookSecret = null);
