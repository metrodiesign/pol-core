using Mediator;

namespace Identity.Application.ResolveTenantUser;

/// <summary>Runtime resolution (REQ-6): maps an authenticated Google subject to its ACTIVE TenantUser and
/// returns the platform-decided tenant + role. Returns null for unregistered / pending / suspended (the
/// host then binds no tenant and denies the request). Runs under the pol_admin bypass connection because
/// it executes BEFORE a tenant is bound.</summary>
public sealed record ResolveTenantUserQuery(string Subject) : IQuery<TenantUserResolution?>;

public sealed record TenantUserResolution(Guid TenantUserId, Guid TenantId, string Role);

public sealed class ResolveTenantUserHandler : IQueryHandler<ResolveTenantUserQuery, TenantUserResolution?>
{
    private readonly ITenantUserRepository _users;

    public ResolveTenantUserHandler(ITenantUserRepository users) => _users = users;

    public async ValueTask<TenantUserResolution?> Handle(ResolveTenantUserQuery query, CancellationToken cancellationToken)
    {
        var user = await _users.GetBySubjectAsync(query.Subject, cancellationToken);
        if (user is null
            || user.Status != Identity.Domain.TenantUserStatus.Active
            || user.TenantId is not { } tenantId
            || user.Role is not { } role)
        {
            return null;
        }

        return new TenantUserResolution(user.Id, tenantId, role.ToString());
    }
}
