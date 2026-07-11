using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;

namespace Worker;

/// <summary>
/// The worker has no HTTP request and no authenticated principal: its actor is whatever the outbox
/// dispatcher binds per message via <see cref="AmbientActor"/>. When nothing is bound (the lease
/// pass), <see cref="HasActor"/> is false so the RLS interceptor leaves <c>SESSION_CONTEXT</c>
/// unset — correct, because the OutboxMessages table is read across merchants. The worker is never the
/// admin cross-merchant principal; per-message it runs RLS-scoped to exactly the message's merchant.
/// </summary>
public sealed class WorkerActorContext : IActorContext
{
    private readonly AmbientActor _ambient;

    public WorkerActorContext(AmbientActor ambient) => _ambient = ambient;

    public Guid MerchantId => _ambient.MerchantId;
    public Guid? UserId => _ambient.UserId;
    public bool HasActor => _ambient.IsBound;
}
