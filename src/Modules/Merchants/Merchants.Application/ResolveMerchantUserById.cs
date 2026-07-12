using Mediator;
using Merchants.Domain;

namespace Merchants.Application;

/// <summary>
/// Per-request, READ-ONLY merchant-user resolution by MerchantUser id (REQ-12.4/17.1). The session carries the
/// account id; the auth handler re-resolves the account's current Status/Merchant/effective permissions FRESH on
/// every request so a suspension/rejection or a role change takes effect within ONE request, without re-login. A
/// non-Active account (suspended/rejected/pending) resolves to <see cref="MerchantUserByIdOutcome.NotActive"/> → the handler
/// denies (REQ-12.4). Runs under the keyed pol_admin (control-plane) connection — merchant-user account tables
/// are control-plane (no merchant predicate, like Admin). The write path (approval) runs only at the admin endpoint, never here.
/// </summary>
public sealed record ResolveMerchantUserByIdQuery(Guid MerchantUserId) : IQuery<MerchantUserByIdResult>;

public enum MerchantUserByIdOutcome { Resolved, NotActive, NotFound }

public sealed record MerchantUserByIdResult(MerchantUserByIdOutcome Outcome, MerchantUserResolution? Resolution, string? Subject)
{
    public static readonly MerchantUserByIdResult NotFound = new(MerchantUserByIdOutcome.NotFound, null, null);
    public static readonly MerchantUserByIdResult NotActive = new(MerchantUserByIdOutcome.NotActive, null, null);
    public static MerchantUserByIdResult Of(MerchantUserResolution resolution, string subject) =>
        new(MerchantUserByIdOutcome.Resolved, resolution, subject);
}

public sealed class ResolveMerchantUserByIdHandler : IQueryHandler<ResolveMerchantUserByIdQuery, MerchantUserByIdResult>
{
    private readonly IMerchantUserRepository _accounts;
    private readonly IMerchantUserRoleRepository _roles;

    public ResolveMerchantUserByIdHandler(IMerchantUserRepository accounts, IMerchantUserRoleRepository roles)
    {
        _accounts = accounts;
        _roles = roles;
    }

    public async ValueTask<MerchantUserByIdResult> Handle(ResolveMerchantUserByIdQuery query, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindByIdAsync(query.MerchantUserId, cancellationToken);
        if (account is null)
            return MerchantUserByIdResult.NotFound;
        // Only an Active account with MerchantId set gets a live request; a suspend/reject denies the NEXT request
        // (REQ-12.4) without waiting for cookie expiry — sessions exist only for Active accounts (REQ-10.1).
        if (account.Status != MerchantUserStatus.Active)
            return MerchantUserByIdResult.NotActive;
        if (account.MerchantId is not { } merchantId)
            return MerchantUserByIdResult.NotActive;
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, merchantId, cancellationToken);
        return MerchantUserByIdResult.Of(new MerchantUserResolution(account.Id, account.Email, merchantId, permissions), account.Subject);
    }
}
