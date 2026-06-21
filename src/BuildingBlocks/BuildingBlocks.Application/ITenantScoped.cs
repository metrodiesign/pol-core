namespace BuildingBlocks.Application;

/// <summary>
/// Marker for a command/query whose data is owned by a single tenant. The
/// <see cref="TenantGuardBehavior{TMessage,TResponse}"/> pipeline rejects such a message when no
/// tenant (and no admin override) is present, before it can reach a handler. This is a
/// convenience guard on top of the data-layer RLS floor (PLAN decision #3), not a replacement.
/// </summary>
public interface ITenantScoped;
