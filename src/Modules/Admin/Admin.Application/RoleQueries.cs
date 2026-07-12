using BuildingBlocks.Application;
using Mediator;

namespace Admin.Application.RoleQueries;

/// <summary>Read models for the role-management console (REQ-1.5, REQ-2). Read-only — no transaction. The role
/// list is the SFS control-plane exemplar: it inherits <see cref="PagedQuery"/> and returns a
/// <see cref="PagedResult{T}"/>. Control-plane data (no MerchantId) -> NOT <c>IMerchantScoped</c>.</summary>
public sealed record ListRolesQuery : PagedQuery, IQuery<PagedResult<AdminRoleListItem>>;

public sealed class ListRolesHandler(IAdminRoleRepository roles)
    : IQueryHandler<ListRolesQuery, PagedResult<AdminRoleListItem>>
{
    public async ValueTask<PagedResult<AdminRoleListItem>> Handle(ListRolesQuery query, CancellationToken ct) =>
        await roles.ListAsync(query, ct);
}

public sealed record GetRoleQuery(string Code) : IQuery<AdminRoleListItem?>;

public sealed class GetRoleHandler(IAdminRoleRepository roles)
    : IQueryHandler<GetRoleQuery, AdminRoleListItem?>
{
    public async ValueTask<AdminRoleListItem?> Handle(GetRoleQuery query, CancellationToken ct) =>
        await roles.GetListItemByCodeAsync(query.Code, ct);
}

public sealed record ListPermissionsQuery : IQuery<PermissionCatalogResult>;

public sealed class ListPermissionsHandler(IAdminRoleRepository roles)
    : IQueryHandler<ListPermissionsQuery, PermissionCatalogResult>
{
    public async ValueTask<PermissionCatalogResult> Handle(ListPermissionsQuery query, CancellationToken ct) =>
        await roles.ListCatalogAsync(ct);
}
