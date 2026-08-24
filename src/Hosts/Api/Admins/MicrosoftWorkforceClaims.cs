using System.Security.Claims;
using Admins.Domain.Users;
using SharedKernel;

namespace Api.Admins;

/// <summary>Validated Microsoft workforce claims used only by the Admin callback.</summary>
internal sealed record MicrosoftWorkforceClaims(
    Guid TenantId,
    string CanonicalEmail)
{
    public ProviderIdentity Identity =>
        new(User.MicrosoftProvider, CanonicalEmail);
}

/// <summary>Pure policy gate for the fixed Admin workforce contract.</summary>
internal static class MicrosoftWorkforceClaimsValidator
{
    internal const string ContextItemKey = "admin.microsoft.workforce-claims";

    public static bool TryValidate(
        ClaimsPrincipal? principal,
        Guid? configuredTenant,
        out MicrosoftWorkforceClaims claims)
    {
        claims = null!;
        if (configuredTenant is not { } tenant || tenant == Guid.Empty || principal is null)
            return false;

        if (!TrySingleUuid(principal, "tid", out var tokenTenant)
            || tokenTenant != tenant
            || !TrySelectIdentifier(principal, out var identifier)
            || !WorkforceEmail.TryCanonicalize(identifier, out var canonicalEmail))
        {
            return false;
        }

        claims = new MicrosoftWorkforceClaims(tokenTenant, canonicalEmail);
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

    private static bool TrySelectIdentifier(ClaimsPrincipal principal, out string identifier)
    {
        identifier = string.Empty;
        var emails = principal.FindAll("email").ToArray();
        if (emails.Length > 1)
            return false;

        if (emails.Length == 1)
        {
            // An email claim is authoritative for selection. Do not fall back when its policy fails.
            identifier = emails[0].Value;
            return true;
        }

        var preferredUsernames = principal.FindAll("preferred_username").ToArray();
        if (preferredUsernames.Length != 1)
            return false;

        identifier = preferredUsernames[0].Value;
        return true;
    }

}

internal sealed class MicrosoftWorkforcePolicyException : Exception { }

internal static class MicrosoftOidcFailureClassifier
{
    internal const string PolicyFailureItemKey = "admin.microsoft.workforce-policy-failure";

    public static void MarkPolicyFailure(HttpContext httpContext) =>
        httpContext.Items[PolicyFailureItemKey] = true;

    public static string BrowserReason(HttpContext httpContext, Exception? failure) =>
        IsPolicyFailure(httpContext, failure) ? "workforce-access-denied" : "auth-failed";

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
