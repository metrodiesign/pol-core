using BuildingBlocks.Application;
using Iam.Application.Permissions;
using Iam.Domain.Permissions;
using Mediator;

namespace Iam.Application.Roles;

/// <summary>Read models for both role-management consoles (REQ-1.5/6.1/6.2/6.4). Read-only — no transaction.
/// Every read is filtered to <see cref="RoleSideContext"/>'s visible set (REQ-3.6/3.9) — a console can never
/// read a role or catalog entry belonging to the other side.</summary>
public sealed record ListRolesQuery : PagedQuery, IQuery<PagedResult<RoleListItem>>
{
    public required RoleSideContext Context { get; init; }
}

public sealed class ListRolesHandler(IRoleStore roles, IRoleAssignmentCounter counter)
    : IQueryHandler<ListRolesQuery, PagedResult<RoleListItem>>
{
    public async ValueTask<PagedResult<RoleListItem>> Handle(ListRolesQuery query, CancellationToken ct)
    {
        var page = await roles.ListAsync(query.Context, query, ct);
        if (page.Items.Count == 0)
            return page;
        var counts = await counter.CountManyAsync(query.Context, [.. page.Items.Select(i => i.Id)], ct);
        return page with { Items = [.. page.Items.Select(i => i with { UserCount = counts.GetValueOrDefault(i.Id) })] };
    }
}

public sealed record GetRoleQuery(RoleSideContext Context, string Code) : IQuery<RoleListItem?>;

public sealed class GetRoleHandler(IRoleStore roles, IRoleAssignmentCounter counter)
    : IQueryHandler<GetRoleQuery, RoleListItem?>
{
    public async ValueTask<RoleListItem?> Handle(GetRoleQuery query, CancellationToken ct)
    {
        var item = await roles.GetListItemByCodeAsync(query.Context, query.Code, ct);
        if (item is null)
            return null;
        return item with { UserCount = await counter.CountAsync(query.Context, item.Id, ct) };
    }
}

/// <summary>Scope, not the full <see cref="RoleSideContext"/> — the catalog carries no per-merchant data, so
/// only which side's vocabulary to return matters (REQ-6.4).</summary>
public sealed record GetPermissionCatalogQuery(Scope Scope) : IQuery<PermissionCatalogResult>;

public sealed class GetPermissionCatalogHandler(IRoleStore roles) : IQueryHandler<GetPermissionCatalogQuery, PermissionCatalogResult>
{
    public async ValueTask<PermissionCatalogResult> Handle(GetPermissionCatalogQuery query, CancellationToken ct) =>
        await roles.ListCatalogAsync(query.Scope, ct);
}
