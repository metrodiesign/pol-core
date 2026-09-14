using Merchants.Domain.Users;

namespace Merchants.Application.Users;

/// <summary>One captured submission. First/last name are always full (REQ-3.3 — the admin must identify the
/// applicant); IdentityNumber/LicenseNumber/Phone/Email are masked unless the request revealed.</summary>
public sealed record AttemptView(
    int AttemptNo, TicketPurpose Purpose, DateTime SubmittedAt,
    string FirstName, string LastName, IdentityType IdentityType,
    string? IdentityNumber, string? SaleCode, string? LicenseNumber, string? Phone,
    string Email, string? PhotoObjectKey, string? PhotoContentType);

/// <summary>
/// Masking rules for merchant-user PII (REQ-3.1/3.2). Deliberately NOT the
/// <c>Orders.Application/GetOrders.MaskIdNumber</c> shape — that one returns <c>*</c> per character for short
/// values (leaks the length); REQ-3.1 pins a constant <c>****</c> instead.
/// </summary>
internal static class PiiMask
{
    /// <summary>null → null; length &gt; 4 → <c>****</c> + last 4; length ≤ 4 → constant <c>****</c>.</summary>
    public static string? Last4(string? value) => value switch
    {
        null => null,
        { Length: > 4 } => $"****{value[^4..]}",
        _ => "****",
    };

    /// <summary>null → null; <c>local@domain</c> → first char + <c>***@</c> + full domain; no <c>@</c> (or
    /// empty local part) → constant <c>****</c> (fail-safe: mask the whole value).</summary>
    public static string? Email(string? value)
    {
        if (value is null)
            return null;
        var at = value.IndexOf('@');
        return at < 1 ? "****" : $"{value[0]}***@{value[(at + 1)..]}";
    }
}
