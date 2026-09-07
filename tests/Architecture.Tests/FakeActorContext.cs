using BuildingBlocks.Application;

namespace Architecture.Tests;

/// <summary>Minimal <see cref="IActorContext"/> test double shared across the rls-to-query-filter read-floor
/// tests — bound to a fixed merchant, or deliberately unbound (REQ-3.1: unbound actor -> Guid.Empty -> 0 rows).</summary>
internal sealed class FakeActorContext : IActorContext
{
    public static readonly FakeActorContext Unbound = new(null);

    public static FakeActorContext For(Guid merchantId) => new(merchantId);

    /// <summary>A merchant user (Tier 1 agent/broker) bound to its merchant — drives the per-user order floor.</summary>
    public static FakeActorContext For(Guid merchantId, Guid userId) => new(merchantId, userId);

    private readonly Guid? _merchantId;
    private readonly Guid? _userId;
    private FakeActorContext(Guid? merchantId, Guid? userId = null)
    {
        _merchantId = merchantId;
        _userId = userId;
    }

    public Guid MerchantId => _merchantId ?? throw new InvalidOperationException("No actor bound.");
    public Guid? UserId => _userId;
    public bool HasActor => _merchantId.HasValue;
}
