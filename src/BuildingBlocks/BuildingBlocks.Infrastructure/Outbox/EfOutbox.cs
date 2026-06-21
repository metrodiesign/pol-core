using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Mediator;

namespace BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// Writes outbox rows into the producer DbContext (tracked, not saved) so they commit atomically
/// with the handler's unit of work. Ids are UUIDv7 for arrival-ordered, index-friendly storage.
/// </summary>
public sealed class EfOutbox : IOutbox
{
    private readonly ProducerDbContext _db;
    private readonly IClock _clock;
    private readonly ITenantContext _tenant;

    public EfOutbox(ProducerDbContext db, IClock clock, ITenantContext tenant)
    {
        _db = db;
        _clock = clock;
        _tenant = tenant;
    }

    public void Enqueue(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // The row's TenantId is the writer's resolved tenant — it must equal SESSION_CONTEXT or the
        // BLOCK-after-insert RLS predicate rejects the insert. A no-tenant write is therefore invalid.
        if (!_tenant.HasTenant)
            throw new InvalidOperationException("Cannot enqueue an outbox message without a bound tenant.");

        var type = notification.GetType();
        var payload = JsonSerializer.Serialize(notification, type, OutboxSerializer.Options);

        _db.OutboxMessages.Add(
            OutboxMessage.Create(Guid.CreateVersion7(), _tenant.TenantId, type.Name, payload, _clock.UtcNow));
    }
}
