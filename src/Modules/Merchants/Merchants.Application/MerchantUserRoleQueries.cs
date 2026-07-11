using Mediator;

namespace Merchants.Application;

/// <summary>Read models for the merchant-user role-management console (REQ-15.4/16). Read-only — no transaction. Bound to
/// the keyed pol_admin context in the API (the catalog/role tables are control-plane).</summary>
// ponytail: DUPLICATE-shaped of Admin.Application.RoleQueries — deliberate debt.
public sealed record ListMerchantUserRolesQuery : IQuery<IReadOnlyList<MerchantUserRoleListItem>>;

public sealed class ListMerchantUserRolesHandler(IMerchantUserRoleRepository roles)
    : IQueryHandler<ListMerchantUserRolesQuery, IReadOnlyList<MerchantUserRoleListItem>>
{
    public async ValueTask<IReadOnlyList<MerchantUserRoleListItem>> Handle(ListMerchantUserRolesQuery query, CancellationToken ct) =>
        await roles.ListAsync(ct);
}

public sealed record GetMerchantUserRoleQuery(string Code) : IQuery<MerchantUserRoleListItem?>;

public sealed class GetMerchantUserRoleHandler(IMerchantUserRoleRepository roles)
    : IQueryHandler<GetMerchantUserRoleQuery, MerchantUserRoleListItem?>
{
    public async ValueTask<MerchantUserRoleListItem?> Handle(GetMerchantUserRoleQuery query, CancellationToken ct) =>
        await roles.GetListItemByCodeAsync(query.Code, ct);
}

public sealed record ListMerchantUserPermissionsQuery : IQuery<MerchantUserPermissionCatalogResult>;

public sealed class ListMerchantUserPermissionsHandler(IMerchantUserRoleRepository roles)
    : IQueryHandler<ListMerchantUserPermissionsQuery, MerchantUserPermissionCatalogResult>
{
    public async ValueTask<MerchantUserPermissionCatalogResult> Handle(ListMerchantUserPermissionsQuery query, CancellationToken ct) =>
        await roles.ListCatalogAsync(ct);
}
