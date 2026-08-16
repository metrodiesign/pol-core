using Mediator;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using SharedKernel;

using Merchants.Application.Users.Roles;

namespace Merchants.Application.Users;

/// <summary>
/// Runtime resolution of an authenticated Google subject to its merchant-user lifecycle state, driving the callback
/// state branch (REQ-9.4). Runs under the keyed pol_admin (control-plane) connection — the
/// <see cref="User"/> table is control-plane (no merchant predicate, like Admin), reachable only via
/// pol_admin. The callback NEVER self-provisions (REQ-9.6): an unknown subject is <see cref="LoginOutcome.NotFound"/>,
/// eligible only for a registration ticket, never an account or a session.
/// </summary>
public sealed record ResolveLoginQuery(ProviderIdentity Identity) : IQuery<LoginResult>;

/// <summary>The login branches (REQ-9.4): <see cref="NotFound"/> → registration ticket; <see cref="Rejected"/> →
/// correction ticket; <see cref="Active"/> → session; <see cref="PendingApproval"/> → 403 "awaiting approval".
/// <see cref="Suspended"/> is a defensive deny (a suspended account never gets a session — REQ-10.1).</summary>
public enum LoginOutcome { NotFound, PendingApproval, Rejected, Suspended, Active }

/// <summary>An ACTIVE merchant-user's identity + reach, materialized once per request into <see cref="IUserScope"/>
/// by the session authentication handler (REQ-17.1). The merchant + effective permissions are resolved fresh from
/// the record, NEVER from a token claim. <paramref name="SaleCode"/> is the account's upstream sale code, carried
/// so the handler can mint the <c>sale_code</c> claim the catalogue path reads (REQ-4.8); optional because an
/// account may not have one bound yet, which the catalogue path answers with 403 (REQ-4.9).</summary>
public sealed record Resolution(
    Guid UserId, string Email, Guid MerchantId, IReadOnlySet<string> Permissions, string? SaleCode = null);

public sealed record LoginResult(LoginOutcome Outcome, Resolution? Resolution)
{
    public static readonly LoginResult NotFound = new(LoginOutcome.NotFound, null);
    public static readonly LoginResult Pending = new(LoginOutcome.PendingApproval, null);
    public static readonly LoginResult Rejected = new(LoginOutcome.Rejected, null);
    public static readonly LoginResult Suspended = new(LoginOutcome.Suspended, null);
    public static LoginResult Active(Resolution resolution) => new(LoginOutcome.Active, resolution);
}

public sealed class ResolveLoginHandler : IQueryHandler<ResolveLoginQuery, LoginResult>
{
    private readonly IAccountResolver _accounts;
    private readonly IRoleRepository _roles;

    // IAccountResolver, never IUserRepository: this runs at the OIDC callback with NO actor bound, and the
    // filtered repository would hide every NULL-MerchantId (pending/rejected) row — and, for an unbound
    // caller, every row entirely (bugfix-merchant-prebind-wiring F1).
    public ResolveLoginHandler(IAccountResolver accounts, IRoleRepository roles)
    {
        _accounts = accounts;
        _roles = roles;
    }

    public async ValueTask<LoginResult> Handle(ResolveLoginQuery query, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindByIdentityAsync(query.Identity, cancellationToken);
        if (account is null)
            return LoginResult.NotFound; // unknown subject → registration ticket only, no self-provision (REQ-9.6)

        if (account.Status is UserStatus.PendingApproval)
            return LoginResult.Pending;
        if (account.Status is UserStatus.Rejected)
            return LoginResult.Rejected;
        if (account.Status is not UserStatus.Active)
            return LoginResult.Suspended; // Suspended → 403

        // Active ALWAYS has MerchantId set (approval sets it — REQ-6.2/9.2); resolve the effective permission set
        // scoped to that merchant (REQ-16.4/17.1). A missing MerchantId on an Active account is an invariant
        // violation → deny (mirrors the former "assignment is null" deny branch, now that the assignment row
        // is absorbed into User.MerchantId).
        if (account.MerchantId is not { } merchantId)
            return LoginResult.Suspended;

        return LoginResult.Active(new Resolution(
            account.UserId, account.Email, merchantId,
            await _roles.ListEffectivePermissionsAsync(account.UserId, merchantId, cancellationToken),
            account.SaleCode));
    }
}
