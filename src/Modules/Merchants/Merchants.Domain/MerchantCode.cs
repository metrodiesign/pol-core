namespace Merchants.Domain;

/// <summary>
/// The captive merchant allowlist (vPrivilege / vCommerce / vSouvenir) and code normalization. Single
/// source of truth for which organization codes may be provisioned. Codes are normalized to
/// lowercase so the unique index, allowlist check and lookups are all case-insensitive.
/// </summary>
public static class MerchantCode
{
    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { "vprivilege", "vcommerce", "vsouvenir" };

    /// <summary>Normalizes a raw code to its canonical form (trimmed + lowercase). Throws on empty.</summary>
    public static string Normalize(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code.Trim().ToLowerInvariant();
    }

    /// <summary>True if the already-normalized code is in the captive allowlist.</summary>
    public static bool IsAllowed(string normalizedCode) => Allowed.Contains(normalizedCode);

    /// <summary>The captive allowlist, for diagnostics / tests.</summary>
    public static IReadOnlyCollection<string> AllowList => (IReadOnlyCollection<string>)Allowed;
}
