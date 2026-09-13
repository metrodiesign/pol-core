using System.Net.Mail;

namespace Admins.Domain.Users;

/// <summary>Normalizes Admin contact data (trim + a standard address/domain-suffix shape) without assigning identity
/// or ownership semantics. A well-formed address is required wherever contact is mandatory; the JIT login path treats
/// a failed normalization as "no email" rather than an error.</summary>
public static class AdminContactEmail
{
    public const int MaxLength = 320;

    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
            return false;

        // Parse with the framework mail parser, reject any display-name/angle-bracket form (the bare address must
        // equal the input), and require the host to carry a real domain suffix (a 2+ letter TLD).
        if (!MailAddress.TryCreate(trimmed, out var address)
            || !string.Equals(address.Address, trimmed, StringComparison.OrdinalIgnoreCase)
            || !HasDomainSuffix(address.Host))
            return false;

        normalized = trimmed;
        return true;
    }

    private static bool HasDomainSuffix(string host)
    {
        var lastDot = host.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == host.Length - 1)
            return false;
        var tld = host.AsSpan(lastDot + 1);
        if (tld.Length < 2)
            return false;
        foreach (var c in tld)
            if (!char.IsLetter(c))
                return false;
        return true;
    }
}
