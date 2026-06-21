using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Scoped holder for an explicitly-bound tenant (see <see cref="ITenantScope"/>). A host's
/// <see cref="ITenantContext"/> consults this first, so a webhook or dispatcher scope that calls
/// <see cref="Begin"/> drives the RLS <c>SESSION_CONTEXT</c> exactly like an authenticated request.
/// Registered Scoped — one binding per unit of work; the binding is cleared when its handle disposes.
/// </summary>
public sealed class AmbientTenant : ITenantScope
{
    private Guid? _tenantId;

    public bool IsBound => _tenantId.HasValue;

    public Guid TenantId =>
        _tenantId ?? throw new InvalidOperationException("No ambient tenant is bound.");

    public IDisposable Begin(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        if (_tenantId.HasValue)
            throw new InvalidOperationException(
                "A tenant is already bound to this scope; nested or re-entrant binding is not allowed.");

        _tenantId = tenantId;
        return new Handle(this);
    }

    private void Clear() => _tenantId = null;

    private sealed class Handle : IDisposable
    {
        private AmbientTenant? _owner;
        public Handle(AmbientTenant owner) => _owner = owner;

        public void Dispose()
        {
            _owner?.Clear();
            _owner = null;
        }
    }
}
