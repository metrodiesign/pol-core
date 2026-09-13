using System.Security.Claims;
using Accounts.Application;
using Accounts.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Api.IdentityAccess;

internal sealed class IdentityAccessRequirement : IAuthorizationRequirement;

internal sealed class IdentityAccessAuthorizationHandler(
    IIdentityAccessQuery identities) : AuthorizationHandler<IdentityAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IdentityAccessRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var accountId)
            || !long.TryParse(context.User.FindFirstValue("authz_version"), out var tokenVersion))
        {
            context.Fail();
            return;
        }

        var account = await identities.FindAccountAsync(accountId, CancellationToken.None);
        if (account is null || account.Status != AccountStatus.Active
            || account.AuthorizationVersion != tokenVersion)
        {
            context.Fail();
            return;
        }

        var clientId = context.User.FindFirstValue("client_id");
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var client = await identities.FindSystemClientAsync(clientId, CancellationToken.None);
            if (client is null || client.Account.Id != account.Id
                || client.Account.Status != AccountStatus.Active
                || client.Client.Status != SystemClientStatus.Active)
            {
                context.Fail();
                return;
            }
        }

        var merchantClaim = context.User.FindFirstValue("merchant_id");
        var tokenContext = context.User.FindFirstValue("token_context");
        AuthorizationSnapshot? authorization = null;
        if (string.Equals(tokenContext, "PLATFORM", StringComparison.Ordinal))
        {
            authorization = await identities.ResolveAuthorizationAsync(
                account.Id, null, clientId is null ? null : Guid.TryParse(clientId, out var platformClient) ? platformClient : null,
                CancellationToken.None);
            if (account.AccountType != AccountType.Employee || authorization?.HasPlatformAccess != true)
            {
                context.Fail();
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(merchantClaim))
        {
            if (!Guid.TryParse(merchantClaim, out var merchantId))
            {
                context.Fail();
                return;
            }
            authorization = await identities.ResolveAuthorizationAsync(
                account.Id, merchantId, null, CancellationToken.None);
            if (authorization?.MerchantId != merchantId || authorization.AccountStatus != AccountStatus.Active)
            {
                context.Fail();
                return;
            }
        }

        var requiredPermission = context.User.FindFirstValue("required_permission");
        if (!string.IsNullOrWhiteSpace(requiredPermission))
        {
            authorization ??= await identities.ResolveAuthorizationAsync(
                account.Id,
                Guid.TryParse(merchantClaim, out var permissionMerchant) ? permissionMerchant : null,
                null,
                CancellationToken.None);
            if (authorization is null || !authorization.Permissions.Contains(requiredPermission, StringComparer.Ordinal))
            {
                context.Fail();
                return;
            }
        }

        context.Succeed(requirement);
    }
}
