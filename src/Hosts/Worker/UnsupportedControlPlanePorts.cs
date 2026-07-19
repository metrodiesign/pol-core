using BuildingBlocks.Application;
using Iam.Application.Permissions;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;

namespace Worker;

/// <summary>
/// The worker has no admin/control-plane surface (task 8.5.6: no <c>ControlPlaneDbContext</c>, no
/// <c>Persistence.Provisioning</c> wiring — role management and merchant provisioning are both
/// HTTP-endpoint-triggered, Api-only flows). But the Mediator source generator registers every handler
/// reachable from Worker's compiled dependency graph regardless of which host actually dispatches it
/// (<c>Iam.Application</c>'s role-CRUD handlers and <c>Merchants.Application</c>'s
/// <c>ProvisionMerchantHandler</c> are both pulled in transitively), so <c>ValidateOnBuild</c> needs
/// SOMETHING constructible for <see cref="IRoleStore"/>/<see cref="IProvisioningWriter"/> even though the
/// worker's own code never sends a request that would resolve one. These throw if ever actually invoked —
/// that would mean a handler meant for the Api host somehow got dispatched from the worker.
/// </summary>
internal sealed class WorkerUnsupportedRoleStore : IRoleStore
{
    private static NotSupportedException Unsupported() =>
        new("IRoleStore has no worker binding — role management is an Api-only, admin-triggered surface.");

    public void Add(Role role) => throw Unsupported();
    public void Remove(Role role) => throw Unsupported();
    public Task<Role?> GetByCodeAsync(RoleSideContext context, string code, CancellationToken cancellationToken) => throw Unsupported();
    public Task<bool> CodeExistsAsync(RoleSideContext context, string code, CancellationToken cancellationToken) => throw Unsupported();
    public Task<PagedResult<RoleListItem>> ListAsync(RoleSideContext context, PagedQuery query, CancellationToken cancellationToken) => throw Unsupported();
    public Task<RoleListItem?> GetListItemByCodeAsync(RoleSideContext context, string code, CancellationToken cancellationToken) => throw Unsupported();
    public Task<PermissionCatalogResult> ListCatalogAsync(Scope scope, CancellationToken cancellationToken) => throw Unsupported();
}

internal sealed class WorkerUnsupportedProvisioningWriter : IProvisioningWriter
{
    public Task<ProvisioningWriteResult> ProvisionAsync(
        ProvisionSpec spec, Guid callerAdminId, long expectedAuthorizationVersion, string operationKey,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "IProvisioningWriter has no worker binding — merchant provisioning is an Api-only, Super-admin-triggered surface.");
}

internal sealed class WorkerUnsupportedRoleAssignmentCounter : IRoleAssignmentCounter
{
    private static NotSupportedException Unsupported() =>
        new("IRoleAssignmentCounter has no worker binding — role management is an Api-only, admin-triggered surface.");

    public Task<int> CountAsync(RoleSideContext context, Guid roleId, CancellationToken cancellationToken) => throw Unsupported();

    public Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(
        RoleSideContext context, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken) => throw Unsupported();
}

/// <summary>The keyed "admin" <see cref="IUnitOfWork"/> role-management handlers require — same "no
/// control-plane surface" rationale as the ports above.</summary>
internal sealed class WorkerUnsupportedAdminUnitOfWork : IUnitOfWork
{
    private static NotSupportedException Unsupported() =>
        new("The keyed \"admin\" IUnitOfWork has no worker binding — it backs Api-only, admin-triggered writes.");

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => throw Unsupported();

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => throw Unsupported();
}
