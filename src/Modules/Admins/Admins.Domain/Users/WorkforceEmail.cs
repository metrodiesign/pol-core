using System.Net.Mail;

namespace Admins.Domain.Users;

/// <summary>Canonical corporate mailbox used by the Tier 0 Microsoft workforce identity.</summary>
public static class WorkforceEmail
{
    public const string Domain = "viriyah.co.th";
    public const int MaxLength = 254;

    public static bool TryCanonicalize(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > MaxLength
            || trimmed.Any(character => !char.IsAscii(character) || char.IsWhiteSpace(character))
            || !MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(parsed.Address, trimmed, StringComparison.Ordinal)
            || !string.Equals(parsed.Host, Domain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        canonical = trimmed.ToLowerInvariant();
        return true;
    }
}
