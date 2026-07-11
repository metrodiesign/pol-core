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
    private readonly PolDbContext _db;
    private readonly IClock _clock;
    private readonly IActorContext _actor;

    public EfOutbox(PolDbContext db, IClock clock, IActorContext actor)
    {
        _db = db;
        _clock = clock;
        _actor = actor;
    }

    public void Enqueue(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // The row's MerchantId is the writer's resolved merchant — it must equal SESSION_CONTEXT or the
        // BLOCK-after-insert RLS predicate rejects the insert. A no-actor write is therefore invalid.
        if (!_actor.HasActor)
            throw new InvalidOperationException("Cannot enqueue an outbox message without a bound actor.");

        var type = notification.GetType();
        var payload = JsonSerializer.Serialize(notification, type, OutboxSerializer.Options);

        _db.OutboxMessages.Add(
            OutboxMessage.Create(Guid.CreateVersion7(), _actor.MerchantId, type.Name, payload, _clock.UtcNow));
    }
}
