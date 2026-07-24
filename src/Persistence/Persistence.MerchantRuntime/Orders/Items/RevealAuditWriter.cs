using Orders.Application;
using Orders.Domain.Lines;

namespace Persistence.MerchantRuntime.Orders.Lines;

/// <summary>
/// Tracks a <see cref="RevealAudit"/> row on the request's own <see cref="MerchantRuntimeDbContext"/> —
/// mirrors <c>MerchantRegistrationAuditWriter.Append</c> (add now, the caller's own <c>IUnitOfWork.
/// SaveChangesAsync</c> persists it). <c>Task</c>-shaped only to match <see cref="IRevealAuditWriter"/>'s
/// port signature; the work itself is synchronous.
/// </summary>
internal sealed class RevealAuditWriter : IRevealAuditWriter
{
    private readonly MerchantRuntimeDbContext _db;

    public RevealAuditWriter(MerchantRuntimeDbContext db) => _db = db;

    public Task AppendAsync(Guid orderLineId, Guid merchantId, string actorType, string actorId,
        string correlationId, CancellationToken cancellationToken)
    {
        _db.Add(RevealAudit.For(orderLineId, merchantId, actorType, actorId, correlationId, DateTime.UtcNow));
        return Task.CompletedTask;
    }
}
