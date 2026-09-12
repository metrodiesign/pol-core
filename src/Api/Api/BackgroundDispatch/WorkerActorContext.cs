using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;

namespace Api.BackgroundDispatch;

/// <summary>
/// The background-dispatch branch of <see cref="IActorContext"/> (multi-tier-deployment task 1 — moved
/// in as-is from the standalone Worker host per design.md's "minimal diff" decision; class name kept).
/// A scope the outbox dispatcher creates has no HTTP request and no authenticated principal: its actor is
/// whatever the dispatcher binds per message via <see cref="AmbientActor"/>. When nothing is bound (the
/// lease pass), <see cref="HasActor"/> is false — correct, because the OutboxMessages/MerchantUserOutbox
/// tables are read across every merchant during that pass. This branch is never the admin cross-merchant
/// principal; per-message it runs scoped to exactly the message's merchant.
/// </summary>
public sealed class WorkerActorContext : IActorContext
{
    private readonly AmbientActor _ambient;

    public WorkerActorContext(AmbientActor ambient) => _ambient = ambient;

    public Guid MerchantId => _ambient.MerchantId;
    public Guid? UserId => _ambient.UserId;
    public bool HasActor => _ambient.IsBound;
}
