using System.Security.Claims;
using Accounts.Application;
using Accounts.Domain;

namespace Api.Iam;

internal sealed record IdentityMerchantAuthorization(
    Guid AccountId,
    Guid MerchantId,
    AuthorizationSnapshot Snapshot,
    bool IsSystemClient);

internal static class IdentityRequestAuthorization
{
    public static async Task<IdentityMerchantAuthorization?> ResolveMerchantAsync(
        ClaimsPrincipal principal,
        IIdentityAccessQuery identities,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue("sub"), out var accountId)
            || !long.TryParse(principal.FindFirstValue("authz_version"), out var tokenVersion))
            return null;

        var account = await identities.FindAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null || account.Status != AccountStatus.Active
            || account.AuthorizationVersion != tokenVersion)
            return null;

        var clientClaim = principal.FindFirstValue("client_id");
        var isSystem = !string.IsNullOrWhiteSpace(clientClaim);
        if (isSystem)
        {
            var client = await identities.FindSystemClientAsync(clientClaim!, cancellationToken)
                .ConfigureAwait(false);
            if (client is null || client.Account.Id != account.Id
                || client.Account.Status != AccountStatus.Active
                || client.Client.Status != SystemClientStatus.Active)
                return null;
        }

        if (!Guid.TryParse(principal.FindFirstValue("merchant_id"), out var merchantId)
            || merchantId == Guid.Empty)
            return null;
        var authorization = await identities.ResolveAuthorizationAsync(
            account.Id, merchantId, clientId: null, cancellationToken).ConfigureAwait(false);
        if (authorization?.MerchantId != merchantId
            || authorization.AccountStatus != AccountStatus.Active)
            return null;
        return new IdentityMerchantAuthorization(account.Id, merchantId, authorization, isSystem);
    }
}
