using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>
/// Runtime resolution of an authenticated Google subject to its producer lifecycle state, driving the callback
/// state branch (REQ-9.4). Runs under the keyed pol_admin (RLS-bypass) connection because a PendingApproval/Rejected
/// <see cref="TenantUser"/> has a NULL <c>TenantId</c> the RLS predicate would hide under a tenant principal
/// (REQ-19.2/19.3). The callback NEVER self-provisions (REQ-9.6): an unknown subject is <see cref="ProducerLoginOutcome.NotFound"/>,
/// eligible only for a registration ticket, never an account or a session.
/// </summary>
public sealed record ResolveLoginQuery(string Subject) : IQuery<ProducerLoginResult>;

/// <summary>The login branches (REQ-9.4): <see cref="NotFound"/> → registration ticket; <see cref="Rejected"/> →
/// correction ticket; <see cref="Active"/> → session; <see cref="PendingApproval"/> → 403 "awaiting approval".
/// <see cref="Suspended"/> is a defensive deny (a suspended account never gets a session — REQ-10.1).</summary>
public enum ProducerLoginOutcome { NotFound, PendingApproval, Rejected, Suspended, Active }

public sealed record ProducerLoginResult(ProducerLoginOutcome Outcome, ProducerResolution? Resolution)
{
    public static readonly ProducerLoginResult NotFound = new(ProducerLoginOutcome.NotFound, null);
    public static readonly ProducerLoginResult Pending = new(ProducerLoginOutcome.PendingApproval, null);
    public static readonly ProducerLoginResult Rejected = new(ProducerLoginOutcome.Rejected, null);
    public static readonly ProducerLoginResult Suspended = new(ProducerLoginOutcome.Suspended, null);
    public static ProducerLoginResult Active(ProducerResolution resolution) => new(ProducerLoginOutcome.Active, resolution);
}

public sealed class ResolveLoginHandler : IQueryHandler<ResolveLoginQuery, ProducerLoginResult>
{
    private readonly ITenantUserRepository _users;
    private readonly IProducerRoleRepository _roles;

    public ResolveLoginHandler(ITenantUserRepository users, IProducerRoleRepository roles)
    {
        _users = users;
        _roles = roles;
    }

    public async ValueTask<ProducerLoginResult> Handle(ResolveLoginQuery query, CancellationToken cancellationToken)
    {
        var user = await _users.FindBySubjectAsync(query.Subject, cancellationToken);
        if (user is null)
            return ProducerLoginResult.NotFound; // unknown subject → registration ticket only, no self-provision (REQ-9.6)

        return user.Status switch
        {
            TenantUserStatus.PendingApproval => ProducerLoginResult.Pending,
            TenantUserStatus.Rejected => ProducerLoginResult.Rejected,
            // Active ALWAYS carries a bound tenant (approval sets it — REQ-6.2); resolve the effective permission set
            // scoped to that tenant (REQ-16.4/17.1). A NULL tenant on an Active row is an invariant violation → deny.
            TenantUserStatus.Active when user.TenantId is { } tenantId =>
                ProducerLoginResult.Active(new ProducerResolution(
                    user.Id, user.Email, tenantId,
                    await _roles.ListEffectivePermissionsAsync(user.Id, tenantId, cancellationToken))),
            _ => ProducerLoginResult.Suspended, // Suspended (or the invariant-violating Active-with-NULL-tenant) → 403
        };
    }
}
