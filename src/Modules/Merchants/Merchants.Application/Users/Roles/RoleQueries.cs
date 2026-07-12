using Mediator;
using Merchants.Application.Users.Permissions;

namespace Merchants.Application.Users.Roles;

/// <summary>Read models for the merchant-user role-management console (REQ-15.4/16). Read-only — no transaction. Bound to
/// the keyed pol_admin context in the API (the catalog/role tables are control-plane).</summary>
// ponytail: DUPLICATE-shaped of Admins.Application.RoleQueries — deliberate debt.
public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleListItem>>;

public sealed class ListRolesHandler(IRoleRepository roles)
    : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleListItem>>
{
    public async ValueTask<IReadOnlyList<RoleListItem>> Handle(ListRolesQuery query, CancellationToken ct) =>
        await roles.ListAsync(ct);
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
