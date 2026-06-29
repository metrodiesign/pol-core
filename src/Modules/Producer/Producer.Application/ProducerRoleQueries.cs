using Mediator;

namespace Producer.Application;

/// <summary>Read models for the producer role-management console (REQ-15.4/16). Read-only — no transaction. Bound to
/// the keyed pol_admin context in the API (the catalog/role tables are control-plane).</summary>
// ponytail: DUPLICATE-shaped of Admin.Application.RoleQueries — deliberate debt.
public sealed record ListProducerRolesQuery : IQuery<IReadOnlyList<ProducerRoleListItem>>;

public sealed class ListProducerRolesHandler(IProducerRoleRepository roles)
    : IQueryHandler<ListProducerRolesQuery, IReadOnlyList<ProducerRoleListItem>>
{
    public async ValueTask<IReadOnlyList<ProducerRoleListItem>> Handle(ListProducerRolesQuery query, CancellationToken ct) =>
        await roles.ListAsync(ct);
}

public sealed record GetProducerRoleQuery(string Code) : IQuery<ProducerRoleListItem?>;

public sealed class GetProducerRoleHandler(IProducerRoleRepository roles)
    : IQueryHandler<GetProducerRoleQuery, ProducerRoleListItem?>
{
    public async ValueTask<ProducerRoleListItem?> Handle(GetProducerRoleQuery query, CancellationToken ct) =>
        await roles.GetListItemByCodeAsync(query.Code, ct);
}

public sealed record ListProducerPermissionsQuery : IQuery<ProducerPermissionCatalogResult>;

public sealed class ListProducerPermissionsHandler(IProducerRoleRepository roles)
    : IQueryHandler<ListProducerPermissionsQuery, ProducerPermissionCatalogResult>
{
    public async ValueTask<ProducerPermissionCatalogResult> Handle(ListProducerPermissionsQuery query, CancellationToken ct) =>
        await roles.ListCatalogAsync(ct);
}
