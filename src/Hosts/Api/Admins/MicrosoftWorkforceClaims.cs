using System.Security.Claims;
using Admins.Domain.Users;

namespace Api.Admins;

/// <summary>Validated tenant-aware Microsoft claims retained only for the current callback request.</summary>
internal sealed record MicrosoftWorkforceClaims(
    Guid TenantId,
    Guid ObjectId,
    string? Email,
    string? EmployeeId = null)
{
    public string Subject => ObjectId.ToString("D");
}

/// <summary>Pure policy gate entered only after framework token and protocol validation succeeds.</summary>
internal static class MicrosoftWorkforceClaimsValidator
{
    internal const string ContextItemKey = "admin.microsoft.workforce-claims";

    public static bool TryValidate(
        ClaimsPrincipal? principal,
        Guid? configuredTenant,
        out MicrosoftWorkforceClaims claims)
    {
        claims = null!;
        if (configuredTenant is not { } tenant || tenant == Guid.Empty || principal is null
            || !TrySingleUuid(principal, "tid", out var tokenTenant)
            || tokenTenant != tenant
            || !TrySingleUuid(principal, "oid", out var objectId))
        {
            return false;
        }

        var emailClaims = principal.FindAll("email").ToArray();
        var email = emailClaims.Length == 1
            && AdminContactEmail.TryNormalize(emailClaims[0].Value, out var normalized)
                ? normalized
                : null;
        claims = new MicrosoftWorkforceClaims(tokenTenant, objectId, email);
        return true;
    }

    private static bool TrySingleUuid(ClaimsPrincipal principal, string type, out Guid value)
    {
        value = Guid.Empty;
        var values = principal.FindAll(type).ToArray();
        return values.Length == 1
            && Guid.TryParse(values[0].Value, out value)
            && value != Guid.Empty;
    }
}

internal sealed class MicrosoftWorkforcePolicyException : Exception { }

internal static class MicrosoftOidcFailureClassifier
{
    internal const string PolicyFailureItemKey = "admin.microsoft.workforce-policy-failure";
    internal const string EmployeeProfileUnavailableItemKey = "admin.microsoft.employee-profile-unavailable";

    public static void MarkPolicyFailure(HttpContext httpContext) =>
        httpContext.Items[PolicyFailureItemKey] = true;

    public static void MarkEmployeeProfileUnavailable(HttpContext httpContext) =>
        httpContext.Items[EmployeeProfileUnavailableItemKey] = true;

    public static string BrowserReason(HttpContext httpContext, Exception? failure) =>
        Find<EmployeeProfileException>(failure)?.Reason
        ?? (IsEmployeeProfileUnavailable(httpContext)
            ? EmployeeProfileException.Unavailable
            : IsPolicyFailure(httpContext, failure) ? "workforce-access-denied" : "auth-failed");

    private static T? Find<T>(Exception? exception)
        where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is T match)
                return match;
        return null;
    }

    private static bool IsEmployeeProfileUnavailable(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(EmployeeProfileUnavailableItemKey, out var marker) && marker is true;

    private static bool IsPolicyFailure(HttpContext httpContext, Exception? failure) =>
        httpContext.Items.TryGetValue(PolicyFailureItemKey, out var marker) && marker is true
        || Contains<MicrosoftWorkforcePolicyException>(failure)
        || Contains<Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException>(failure);

    private static bool Contains<T>(Exception? exception)
        where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is T)
                return true;
        return false;
    }
}
