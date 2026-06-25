using Mediator;

namespace Admin.Application.RoleQueries;

/// <summary>Read models for the role-management console (REQ-1.5, REQ-2). Read-only — no transaction.</summary>
public sealed record ListRolesQuery : IQuery<IReadOnlyList<AdminRoleListItem>>;

public sealed class ListRolesHandler(IAdminRoleRepository roles)
    : IQueryHandler<ListRolesQuery, IReadOnlyList<AdminRoleListItem>>
{
    public async ValueTask<IReadOnlyList<AdminRoleListItem>> Handle(ListRolesQuery query, CancellationToken ct) =>
        await roles.ListAsync(ct);
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
