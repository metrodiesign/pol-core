using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>
/// Runtime resolution of an authenticated Google subject to its producer lifecycle state, driving the callback
/// state branch (REQ-9.4). Runs under the keyed pol_admin (control-plane) connection — the
/// <see cref="ProducerAccount"/> table is control-plane (no tenant predicate, like Admin), reachable only via
/// pol_admin. The callback NEVER self-provisions (REQ-9.6): an unknown subject is <see cref="ProducerLoginOutcome.NotFound"/>,
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
    private readonly IProducerAccountRepository _accounts;
    private readonly IProducerTenantAssignmentRepository _assignments;
    private readonly IProducerRoleRepository _roles;

    public ResolveLoginHandler(IProducerAccountRepository accounts, IProducerTenantAssignmentRepository assignments,
        IProducerRoleRepository roles)
    {
        _accounts = accounts;
        _assignments = assignments;
        _roles = roles;
    }

    public async ValueTask<ProducerLoginResult> Handle(ResolveLoginQuery query, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindBySubjectAsync(query.Subject, cancellationToken);
        if (account is null)
            return ProducerLoginResult.NotFound; // unknown subject → registration ticket only, no self-provision (REQ-9.6)

        if (account.Status is ProducerAccountStatus.PendingApproval)
            return ProducerLoginResult.Pending;
        if (account.Status is ProducerAccountStatus.Rejected)
            return ProducerLoginResult.Rejected;
        if (account.Status is not ProducerAccountStatus.Active)
            return ProducerLoginResult.Suspended; // Suspended → 403

        // Active ALWAYS has a tenant assignment (approval creates it — REQ-6.2); resolve the effective permission set
        // scoped to that tenant (REQ-16.4/17.1). A missing assignment on an Active account is an invariant violation → deny.
        var assignment = await _assignments.FindByAccountIdAsync(account.Id, cancellationToken);
        if (assignment is null)
            return ProducerLoginResult.Suspended;

        return ProducerLoginResult.Active(new ProducerResolution(
            account.Id, account.Email, assignment.TenantId,
            await _roles.ListEffectivePermissionsAsync(account.Id, assignment.TenantId, cancellationToken)));
    }
}
