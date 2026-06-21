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

    public EfOutbox(ProducerDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Enqueue(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var type = notification.GetType();
        var payload = JsonSerializer.Serialize(notification, type, OutboxSerializer.Options);

        _db.OutboxMessages.Add(OutboxMessage.Create(Guid.CreateVersion7(), type.Name, payload, _clock.UtcNow));
    }
}
