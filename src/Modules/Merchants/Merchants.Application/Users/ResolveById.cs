using Mediator;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

using Merchants.Application.Users.Roles;

namespace Merchants.Application.Users;

/// <summary>
/// Per-request, READ-ONLY merchant-user resolution by User id (REQ-12.4/17.1). The session carries the
/// account id; the auth handler re-resolves the account's current Status/Merchant/effective permissions FRESH on
/// every request so a suspension/rejection or a role change takes effect within ONE request, without re-login. A
/// non-Active account (suspended/rejected/pending) resolves to <see cref="ByIdOutcome.NotActive"/> → the handler
/// denies (REQ-12.4). Runs under the keyed pol_admin (control-plane) connection — merchant-user account tables
/// are control-plane (no merchant predicate, like Admin). The write path (approval) runs only at the admin endpoint, never here.
/// </summary>
public sealed record ResolveByIdQuery(Guid MerchantUserId) : IQuery<ByIdResult>;

public enum ByIdOutcome { Resolved, NotActive, NotFound }

public sealed record ByIdResult(ByIdOutcome Outcome, Resolution? Resolution, string? Subject)
{
    public static readonly ByIdResult NotFound = new(ByIdOutcome.NotFound, null, null);
    public static readonly ByIdResult NotActive = new(ByIdOutcome.NotActive, null, null);
    public static ByIdResult Of(Resolution resolution, string subject) =>
        new(ByIdOutcome.Resolved, resolution, subject);
}

public sealed class ResolveByIdHandler : IQueryHandler<ResolveByIdQuery, ByIdResult>
{
    private readonly IAccountResolver _accounts;
    private readonly IRoleRepository _roles;

    // IAccountResolver, never IUserRepository: this runs INSIDE session authentication, before the
    // merchant_id claim exists on the request — pre-bind by construction (bugfix-merchant-prebind-wiring F6).
    public ResolveByIdHandler(IAccountResolver accounts, IRoleRepository roles)
    {
        _accounts = accounts;
        _roles = roles;
    }

    public async ValueTask<ByIdResult> Handle(ResolveByIdQuery query, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindByIdAsync(query.MerchantUserId, cancellationToken);
        if (account is null)
            return ByIdResult.NotFound;
        // Only an Active account with MerchantId set gets a live request; a suspend/reject denies the NEXT request
        // (REQ-12.4) without waiting for cookie expiry — sessions exist only for Active accounts (REQ-10.1).
        if (account.Status != UserStatus.Active)
            return ByIdResult.NotActive;
        if (account.MerchantId is not { } merchantId)
            return ByIdResult.NotActive;
        var permissions = await _roles.ListEffectivePermissionsAsync(account.MerchantUserId, merchantId, cancellationToken);
        return ByIdResult.Of(new Resolution(account.MerchantUserId, account.Email, merchantId, permissions), account.Subject);
    }
}
