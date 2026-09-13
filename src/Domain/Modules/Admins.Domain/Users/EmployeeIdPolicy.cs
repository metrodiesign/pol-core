namespace Admins.Domain.Users;

/// <summary>Outcome of <see cref="EmployeeIdPolicy.TryNormalize"/> (tier0-graph-employee-profile REQ-2.1-2.4, 2.16).</summary>
public enum EmployeeIdCheck { Ok, Missing, Invalid }

/// <summary>
/// Pure normalisation of the Microsoft Graph <c>employeeId</c> before it touches any lookup, compare or
/// persist path: trim (REQ-2.1) -> empty = Missing (REQ-2.2) -> control character / inner whitespace
/// (REQ-2.3) or longer than <see cref="MaxLength"/> (REQ-2.4) = Invalid -> invariant uppercase (REQ-2.16).
/// </summary>
public static class EmployeeIdPolicy
{
    public const int MaxLength = 16;

    public static EmployeeIdCheck TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        var trimmed = raw?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return EmployeeIdCheck.Missing;
        if (trimmed.Length > MaxLength || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return EmployeeIdCheck.Invalid;
        normalized = trimmed.ToUpperInvariant();
        return EmployeeIdCheck.Ok;
    }
}
