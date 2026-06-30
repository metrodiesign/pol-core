using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>
/// Per-request, READ-ONLY producer resolution by ProducerAccount id (REQ-12.4/17.1). The session carries the
/// account id; the auth handler re-resolves the account's current Status/Tenant/effective permissions FRESH on
/// every request so a suspension/rejection or a role change takes effect within ONE request, without re-login. A
/// non-Active account (suspended/rejected/pending) resolves to <see cref="ProducerByIdOutcome.NotActive"/> → the handler
/// denies (REQ-12.4). Runs under the keyed pol_admin (control-plane) connection — producer account/assignment tables
/// are control-plane (no tenant predicate, like Admin). The write path (approval) runs only at the admin endpoint, never here.
/// </summary>
public sealed record ResolveProducerByIdQuery(Guid TenantUserId) : IQuery<ProducerByIdResult>;

public enum ProducerByIdOutcome { Resolved, NotActive, NotFound }

public sealed record ProducerByIdResult(ProducerByIdOutcome Outcome, ProducerResolution? Resolution, string? Subject)
{
    public static readonly ProducerByIdResult NotFound = new(ProducerByIdOutcome.NotFound, null, null);
    public static readonly ProducerByIdResult NotActive = new(ProducerByIdOutcome.NotActive, null, null);
    public static ProducerByIdResult Of(ProducerResolution resolution, string subject) =>
        new(ProducerByIdOutcome.Resolved, resolution, subject);
}

public sealed class ResolveProducerByIdHandler : IQueryHandler<ResolveProducerByIdQuery, ProducerByIdResult>
{
    private readonly IProducerAccountRepository _accounts;
    private readonly IProducerTenantAssignmentRepository _assignments;
    private readonly IProducerRoleRepository _roles;

    public ResolveProducerByIdHandler(IProducerAccountRepository accounts,
        IProducerTenantAssignmentRepository assignments, IProducerRoleRepository roles)
    {
        _accounts = accounts;
        _assignments = assignments;
        _roles = roles;
    }

    public async ValueTask<ProducerByIdResult> Handle(ResolveProducerByIdQuery query, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindByIdAsync(query.TenantUserId, cancellationToken);
        if (account is null)
            return ProducerByIdResult.NotFound;
        // Only an Active account with a tenant assignment gets a live request; a suspend/reject denies the NEXT request
        // (REQ-12.4) without waiting for cookie expiry — sessions exist only for Active accounts (REQ-10.1).
        if (account.Status != ProducerAccountStatus.Active)
            return ProducerByIdResult.NotActive;
        var assignment = await _assignments.FindByAccountIdAsync(account.Id, cancellationToken);
        if (assignment is null)
            return ProducerByIdResult.NotActive;
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, assignment.TenantId, cancellationToken);
        return ProducerByIdResult.Of(new ProducerResolution(account.Id, account.Email, assignment.TenantId, permissions), account.Subject);
    }
}
