using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;

namespace Persistence.MerchantRuntime.Payments.Psp;

/// <summary>EF Core repository for <see cref="Connection"/> over the MerchantRuntime data plane.</summary>
internal sealed class ConnectionRepository : IConnectionRepository
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly ISecurityTelemetry _telemetry;

    public ConnectionRepository(MerchantRuntimeDbContext db, ISecurityTelemetry telemetry)
    {
        _db = db;
        _telemetry = telemetry;
    }

    public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken cancellationToken) =>
        _db.Set<Connection>()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.Psp == psp, cancellationToken);

    public Task<Connection?> GetByIdAsync(Guid pspConnectionId, CancellationToken cancellationToken) =>
        _db.Set<Connection>()
            .FirstOrDefaultAsync(x => x.Id == pspConnectionId, cancellationToken);

    public void Add(Connection connection) => _db.Set<Connection>().Add(connection);

    // task 8.5.8: this is the ADMIN cross-merchant read-back (GetMerchantHandler, the only caller) — the
    // caller's own IActorContext has no merchant of its own (an admin request), so the ambient read floor
    // would otherwise return zero rows regardless of the explicit merchantId below. Under the old RLS model
    // this ran on the separate pol_admin (bypass) connection instead; IgnoreQueryFilters() is the app-layer
    // equivalent now that there is only one principal.
    public async Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        _telemetry.Emit(new DenialEvent(
            DenialCategory.AdminCrossMerchantAction, "admin", ActorId: null, merchantId, nameof(Connection),
            "ListByTenant", "Admin read-back crossed the merchant read floor via IgnoreQueryFilters().",
            CorrelationId.Current, DateTime.UtcNow));

        return await _db.Set<Connection>().IgnoreQueryFilters()
            .Where(x => x.MerchantId == merchantId)
            .ToListAsync(cancellationToken);
    }
}
