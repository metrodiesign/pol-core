using System.Security.Claims;
using System.Text.Encodings.Web;
using Access.Domain;
using Accounts.Application;
using Accounts.Domain;
using Api.Admins;
using Api.Iam;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;

namespace Api.IdentityAccess;

/// <summary>The one Bearer scheme of the platform. OpenIddict validates the JWT (signature, audience, lifetime,
/// token and authorization entries); this handler then does what a session store does on every request:
/// <list type="bullet">
/// <item>The account must exist, be Active, and still be at the authorization version the token was issued
/// with; otherwise the request is unauthenticated (401), and the SPA refreshes (a refresh re-syncs the
/// version) or logs in again.</item>
/// <item>When the "admin" policy selected this scheme (employee on the admin console), the route's handlers
/// read <see cref="IAdminScope"/>, so it is materialized from the account's authorization snapshot: AdminId =
/// AccountId, permissions = the platform-role permission set, and platform access = the old Super tier
/// (unrestricted merchant reach); an employee without platform access is Scoped to its active MerchantAccess
/// set. Identity-* and order routes never set the Admin audience and stay unbound (their write floor is the
/// unbound path). A SYSTEM or non-employee account never binds, so it never reaches the admin console.</item>
/// </list>
/// An invalid or expired JWT is challenged by OpenIddict itself (401 with its error detail); a valid JWT that fails
/// the checks above is challenged here as <c>invalid_token</c> (401) so the SPA refreshes instead of treating a
/// stale token as a permission error (OpenIddict would answer a challenge for a valid token with 403).</summary>
internal sealed class PlatformTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IIdentityAccessQuery identities,
    AdminScope adminScope)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PlatformToken";
    private const string RejectedItem = "pol.platform-token.rejected";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var inner = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!inner.Succeeded || inner.Principal is null)
            return inner;

        var principal = inner.Principal;
        if (!Guid.TryParse(principal.FindFirstValue("sub"), out var accountId)
            || !long.TryParse(principal.FindFirstValue("authz_version"), out var tokenVersion))
            return Reject("The platform token has no account context.");

        var account = await identities.FindAccountAsync(accountId, Context.RequestAborted);
        if (account is null || account.Status != AccountStatus.Active || account.AuthorizationVersion != tokenVersion)
            return Reject("The platform token is stale.");

        if (Context.Features.Get<SelectedConsoleAudience>()?.Value == ConsoleAudience.Admin
            && !adminScope.IsBound
            && !await TryBindAdminScopeAsync(account))
            return Reject("Admin console requires an employee account.");

        return AuthenticateResult.Success(new AuthenticationTicket(principal, inner.Properties, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items[RejectedItem] is string reason)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"{reason}\"";
            return Task.CompletedTask;
        }
        return Context.ChallengeAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, properties);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Context.ForbidAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, properties);

    private AuthenticateResult Reject(string reason)
    {
        Context.Items[RejectedItem] = reason;
        return AuthenticateResult.Fail(reason);
    }

    private async Task<bool> TryBindAdminScopeAsync(Account account)
    {
        if (account.AccountType != AccountType.Employee)
            return false;
        var snapshot = await identities.ResolveAuthorizationAsync(
            account.Id, merchantId: null, clientId: null, Context.RequestAborted);
        if (snapshot is null)
            return false;
        var accessible = snapshot.HasPlatformAccess
            ? global::Admins.Application.Users.AccessibleMerchants.All
            : global::Admins.Application.Users.AccessibleMerchants.Of(
                (await identities.ListMerchantAccessAsync(account.Id, Context.RequestAborted))
                    .Where(x => x.Status == AccessStatus.Active)
                    .Select(x => x.MerchantId)
                    .ToHashSet());
        adminScope.Set(new global::Admins.Application.Users.Resolution(
            account.Id,
            await identities.FindLoginEmailAsync(account.Id, Context.RequestAborted),
            snapshot.HasPlatformAccess ? global::Admins.Domain.Users.Tier.Super : global::Admins.Domain.Users.Tier.Scoped,
            accessible)
        {
            Permissions = snapshot.Permissions,
            AuthorizationVersion = account.AuthorizationVersion,
        });
        return true;
    }
}
