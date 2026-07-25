using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain.Items;
using OrderItem = Orders.Domain.Items.Item;

namespace Persistence.MerchantRuntime.Orders.Items;

/// <summary>
/// EF Core implementation of <see cref="IAdminItemPolicyWriter"/> — the admin cross-merchant write
/// escape-hatch for <see cref="ItemPolicy"/> (policy-reference-record REQ-3.2-admin/3.3-admin). Bound to its
/// OWN <see cref="MerchantRuntimeDbContext"/> instance (built by <c>AddAdminItemPolicyWriter</c>, mirror
/// <c>Persistence.Provisioning</c>'s <c>AddProvisioning</c>) — NEVER the ambient one
/// <see cref="ItemPolicyRepository"/> uses, which is bound to <c>MerchantRequestWriteAuthorizer</c> and would
/// deny every write from an admin request (no bound merchant). Every read here explicitly bypasses the
/// merchant query filter (<c>IgnoreQueryFilters()</c>) — allowlisted in
/// <c>Architecture.Tests.BypassPrimitiveTests</c> — and emits one <see cref="DenialEvent"/> per cross-floor
/// load (pattern <c>ConnectionRepository.ListByTenantAsync</c>). The actual write-floor confinement for a
/// Scoped admin is <c>Api.Persistence.AdminItemPolicyWriteAuthorizer.CanWrite</c> (checked at
/// <see cref="SaveChangesAsync"/>), not this class — this class only crosses the READ floor.
/// </summary>
internal sealed class AdminItemPolicyWriter : IAdminItemPolicyWriter, IAsyncDisposable
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISecurityTelemetry _telemetry;

    public AdminItemPolicyWriter(MerchantRuntimeDbContext db, ISecurityTelemetry telemetry)
    {
        _db = db;
        _unitOfWork = new MerchantRuntimeUnitOfWork(db, telemetry);
        _telemetry = telemetry;
    }

    public async Task<AdminItemPolicyLoad> LoadAsync(Guid orderItemId, CancellationToken cancellationToken)
    {
        _telemetry.Emit(new DenialEvent(
            DenialCategory.AdminCrossMerchantAction, "admin", ActorId: null, TargetMerchant: null,
            nameof(OrderItem), "LoadItemWithMerchant",
            "Admin cross-merchant ItemPolicy write crossed the merchant read floor via IgnoreQueryFilters().",
            CorrelationId.Current, DateTime.UtcNow));

        var item = await _db.Set<OrderItem>().IgnoreQueryFilters()
            .Where(i => i.Id == orderItemId)
            .Select(i => new { i.Id, i.MerchantId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (item is null)
            return AdminItemPolicyLoad.NotFound;

        var policy = await _db.Set<ItemPolicy>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.OrderItemId == orderItemId, cancellationToken).ConfigureAwait(false);

        return new AdminItemPolicyLoad(true, item.MerchantId, policy);
    }

    public void Add(ItemPolicy policy) => _db.Set<ItemPolicy>().Add(policy);

    public void AddAudit(ItemPolicyAudit audit) => _db.Set<ItemPolicyAudit>().Add(audit);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _unitOfWork.SaveChangesAsync(cancellationToken);

    // This context is manually constructed by AddAdminItemPolicyWriter (never itself registered in DI), so
    // nothing else disposes it — owning that lifecycle here is what lets the DI container still dispose it
    // correctly (it disposes whatever IAsyncDisposable/IDisposable instance a Scoped factory RETURNS, which
    // is this class, at end of request scope).
    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
