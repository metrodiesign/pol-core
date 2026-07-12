using Admins.Application.Permissions;
using BuildingBlocks.Application;
using Mediator;

namespace Admins.Application.Roles;

/// <summary>Read models for the role-management console (REQ-1.5, REQ-2). Read-only — no transaction. The role
/// list is the SFS control-plane exemplar: it inherits <see cref="PagedQuery"/> and returns a
/// <see cref="PagedResult{T}"/>. Control-plane data (no MerchantId) -> NOT <c>IMerchantScoped</c>.</summary>
public sealed record ListRolesQuery : PagedQuery, IQuery<PagedResult<RoleListItem>>;

public sealed class ListRolesHandler(IRoleRepository roles)
    : IQueryHandler<ListRolesQuery, PagedResult<RoleListItem>>
{
    public async ValueTask<PagedResult<RoleListItem>> Handle(ListRolesQuery query, CancellationToken ct) =>
        await roles.ListAsync(query, ct);
}

public sealed record GetRoleQuery(string Code) : IQuery<RoleListItem?>;

public sealed class GetRoleHandler(IRoleRepository roles)
    : IQueryHandler<GetRoleQuery, RoleListItem?>
{
    public async ValueTask<RoleListItem?> Handle(GetRoleQuery query, CancellationToken ct) =>
        await roles.GetListItemByCodeAsync(query.Code, ct);
}

public sealed record ListPermissionsQuery : IQuery<PermissionCatalogResult>;

public sealed class ListPermissionsHandler(IRoleRepository roles)
    : IQueryHandler<ListPermissionsQuery, PermissionCatalogResult>
{
    public async ValueTask<PermissionCatalogResult> Handle(ListPermissionsQuery query, CancellationToken ct) =>
        await roles.ListCatalogAsync(ct);
}
