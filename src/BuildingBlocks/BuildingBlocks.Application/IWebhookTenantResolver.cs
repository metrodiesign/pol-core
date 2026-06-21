namespace BuildingBlocks.Application;

/// <summary>
/// Maps a trusted PSP connection id to its owning tenant for the unauthenticated webhook path.
/// The webhook has no tenant claim yet RLS hides every <c>PspConnections</c> row until a tenant is
/// bound — a chicken-and-egg. The implementation resolves the id through a stored procedure that runs
/// as a bypass principal, so it can read ONLY the one mapping row and return ONLY the tenant id; a
/// tenant principal still cannot read connections directly. The caller then binds that tenant
/// (<see cref="ITenantScope"/>) before sending the webhook command, so all further work is RLS-scoped.
/// </summary>
public interface IWebhookTenantResolver
{
    /// <summary>Returns the connection's tenant, or null if no such connection exists.</summary>
    Task<Guid?> ResolveTenantAsync(Guid pspConnectionId, CancellationToken cancellationToken);
}
