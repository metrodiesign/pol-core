namespace SharedKernel;

/// <summary>
/// Minimal ISO 4217 registry used by <see cref="Money"/> to validate currency codes and
/// know each code's minor-unit exponent (THB=2 satang, JPY=0). This is the platform-wide
/// allowlist; per-tenant / per-PSP currency allowlists narrow it further at their own seam.
/// </summary>
public static class Iso4217
{
    private static readonly IReadOnlyDictionary<string, int> Currencies =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["THB"] = 2,
            ["USD"] = 2,
            ["JPY"] = 0,
        };

    public static bool IsSupported(string code) => Currencies.ContainsKey(code);

    public static int MinorUnitDigits(string code) =>
        Currencies.TryGetValue(code, out var digits)
            ? digits
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown ISO 4217 currency.");

    public static IReadOnlyCollection<string> Supported => (IReadOnlyCollection<string>)Currencies.Keys;
}
