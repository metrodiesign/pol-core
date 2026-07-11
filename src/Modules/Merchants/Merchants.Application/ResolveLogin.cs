using Mediator;
using Merchants.Domain;

namespace Merchants.Application;

/// <summary>
/// Runtime resolution of an authenticated Google subject to its merchant-user lifecycle state, driving the callback
/// state branch (REQ-9.4). Runs under the keyed pol_admin (control-plane) connection — the
/// <see cref="MerchantUser"/> table is control-plane (no merchant predicate, like Admin), reachable only via
/// pol_admin. The callback NEVER self-provisions (REQ-9.6): an unknown subject is <see cref="MerchantUserLoginOutcome.NotFound"/>,
/// eligible only for a registration ticket, never an account or a session.
/// </summary>
public sealed record ResolveLoginQuery(string Subject) : IQuery<MerchantUserLoginResult>;

/// <summary>The login branches (REQ-9.4): <see cref="NotFound"/> → registration ticket; <see cref="Rejected"/> →
/// correction ticket; <see cref="Active"/> → session; <see cref="PendingApproval"/> → 403 "awaiting approval".
/// <see cref="Suspended"/> is a defensive deny (a suspended account never gets a session — REQ-10.1).</summary>
public enum MerchantUserLoginOutcome { NotFound, PendingApproval, Rejected, Suspended, Active }

public sealed record MerchantUserLoginResult(MerchantUserLoginOutcome Outcome, MerchantUserResolution? Resolution)
{
    public static readonly MerchantUserLoginResult NotFound = new(MerchantUserLoginOutcome.NotFound, null);
    public static readonly MerchantUserLoginResult Pending = new(MerchantUserLoginOutcome.PendingApproval, null);
    public static readonly MerchantUserLoginResult Rejected = new(MerchantUserLoginOutcome.Rejected, null);
    public static readonly MerchantUserLoginResult Suspended = new(MerchantUserLoginOutcome.Suspended, null);
    public static MerchantUserLoginResult Active(MerchantUserResolution resolution) => new(MerchantUserLoginOutcome.Active, resolution);
}

public sealed class ResolveLoginHandler : IQueryHandler<ResolveLoginQuery, MerchantUserLoginResult>
{
    private readonly IMerchantUserRepository _accounts;
    private readonly IMerchantUserRoleRepository _roles;

    public ResolveLoginHandler(IMerchantUserRepository accounts, IMerchantUserRoleRepository roles)
    {
        _accounts = accounts;
        _roles = roles;
    }

    public async ValueTask<MerchantUserLoginResult> Handle(ResolveLoginQuery query, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindBySubjectAsync(query.Subject, cancellationToken);
        if (account is null)
            return MerchantUserLoginResult.NotFound; // unknown subject → registration ticket only, no self-provision (REQ-9.6)

        if (account.Status is MerchantUserStatus.PendingApproval)
            return MerchantUserLoginResult.Pending;
        if (account.Status is MerchantUserStatus.Rejected)
            return MerchantUserLoginResult.Rejected;
        if (account.Status is not MerchantUserStatus.Active)
            return MerchantUserLoginResult.Suspended; // Suspended → 403

        // Active ALWAYS has MerchantId set (approval sets it — REQ-6.2/9.2); resolve the effective permission set
        // scoped to that merchant (REQ-16.4/17.1). A missing MerchantId on an Active account is an invariant
        // violation → deny (mirrors the former "assignment is null" deny branch, now that the assignment row
        // is absorbed into MerchantUser.MerchantId).
        if (account.MerchantId is not { } merchantId)
            return MerchantUserLoginResult.Suspended;

        return MerchantUserLoginResult.Active(new MerchantUserResolution(
            account.Id, account.Email, merchantId,
            await _roles.ListEffectivePermissionsAsync(account.Id, merchantId, cancellationToken)));
    }
}
