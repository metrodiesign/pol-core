using Mediator;

namespace BuildingBlocks.Application;

/// <summary>
/// Transactional outbox writer (PLAN decision #10). A handler enqueues an integration event in
/// the SAME unit of work as the state change it describes; a background dispatcher later publishes
/// it at-least-once. <see cref="Enqueue"/> only tracks the row — the handler's
/// <c>SaveChanges</c> commits it atomically with the rest of the transaction.
/// </summary>
public interface IOutbox
{
    void Enqueue(INotification notification);
}
