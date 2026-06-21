namespace BuildingBlocks.Application;

/// <summary>
/// Binds a tenant explicitly for entry points that do NOT carry an authenticated tenant claim:
/// the PSP webhook (tenant resolved from the connection id) and the outbox dispatcher (tenant taken
/// from the message). HTTP requests never use this — their tenant comes from the verified principal.
/// <para>
/// The binding feeds <see cref="ITenantContext"/>, so it must be set BEFORE the unit of work opens
/// its first connection (the RLS interceptor stamps <c>SESSION_CONTEXT('TenantId')</c> at open).
/// <see cref="Begin"/> throws if a tenant is already bound (a confused-deputy guard) and the returned
/// handle clears the binding on dispose.
/// </para>
/// </summary>
public interface ITenantScope
{
    IDisposable Begin(Guid tenantId);
}
