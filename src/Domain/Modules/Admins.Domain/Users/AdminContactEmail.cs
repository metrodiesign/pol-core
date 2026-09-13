namespace Admins.Domain.Users;

/// <summary>Normalizes optional Admin contact data without assigning identity or ownership semantics.</summary>
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

        normalized = trimmed;
        return true;
    }
}
