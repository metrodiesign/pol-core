namespace BuildingBlocks.Application;

/// <summary>
/// Ambient per-request tenant identity. Resolved at the host edge from the authenticated
/// principal (never from a URL path before signature verification — PLAN decision #4) and
/// used by the data layer to set <c>SESSION_CONTEXT('TenantId')</c> at connection open
/// (PLAN decision #3). Registered Scoped; anything depending on it must also be Scoped.
/// </summary>
public interface ITenantContext
{
    /// <summary>The active tenant. Throws if <see cref="HasTenant"/> is false.</summary>
    Guid TenantId { get; }

    /// <summary>True when a concrete tenant is bound to this request.</summary>
    bool HasTenant { get; }
}
