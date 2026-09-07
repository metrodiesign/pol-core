using SharedKernel;

namespace Payments.Domain.Psp;

/// <summary>
/// Omise's key-prefix contract: <c>skey_test_</c> keys only work against the test surface, every other
/// prefix is live. The control plane applies it BEFORE a secret reaches the vault (REQ-4.8) and the adapter
/// re-applies it before every request (REQ-2.5), so a key from the wrong environment can never be sent.
/// </summary>
public static class OmiseSecretKeys
{
    public static bool IsTestKey(string secretKey) =>
        secretKey.StartsWith("skey_test_", StringComparison.Ordinal);

    public static bool MatchesEnvironment(string secretKey, PspEnvironment environment) =>
        IsTestKey(secretKey) == (environment == PspEnvironment.Sandbox);
}
