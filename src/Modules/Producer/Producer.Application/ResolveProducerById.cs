using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>
/// Per-request, READ-ONLY producer resolution by TenantUser id (REQ-12.4/17.1). The session carries the
/// <c>TenantUserId</c>; the auth handler re-resolves the user's current Status/Tenant/effective permissions FRESH on
/// every request so a suspension/rejection or a role change takes effect within ONE request, without re-login. A
/// non-Active user (suspended/rejected/pending) resolves to <see cref="ProducerByIdOutcome.NotActive"/> → the handler
/// denies (REQ-12.4). Runs under the keyed pol_admin (RLS-bypass) connection — producer identity tables are read
/// cross-tenant from the session. The write path (approval) runs only at the admin endpoint, never here.
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
    private readonly ITenantUserRepository _users;
    private readonly IProducerRoleRepository _roles;

    public ResolveProducerByIdHandler(ITenantUserRepository users, IProducerRoleRepository roles)
    {
        _users = users;
        _roles = roles;
    }

    public async ValueTask<ProducerByIdResult> Handle(ResolveProducerByIdQuery query, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(query.TenantUserId, cancellationToken);
        if (user is null)
            return ProducerByIdResult.NotFound;
        // Only an Active user with a bound tenant gets a live request; a suspend/reject denies the NEXT request
        // (REQ-12.4) without waiting for cookie expiry — sessions exist only for Active users (REQ-10.1).
        if (user.Status != TenantUserStatus.Active || user.TenantId is not { } tenantId)
            return ProducerByIdResult.NotActive;
        var permissions = await _roles.ListEffectivePermissionsAsync(user.Id, tenantId, cancellationToken);
        return ProducerByIdResult.Of(new ProducerResolution(user.Id, user.Email, tenantId, permissions), user.Subject);
    }
}
