using BuildingBlocks.Application;

namespace Architecture.Tests;

/// <summary>Minimal <see cref="IActorContext"/> test double shared across the rls-to-query-filter read-floor
/// tests — bound to a fixed merchant, or deliberately unbound (REQ-3.1: unbound actor -> Guid.Empty -> 0 rows).</summary>
internal sealed class FakeActorContext : IActorContext
{
    public static readonly FakeActorContext Unbound = new(null);

    public static FakeActorContext For(Guid merchantId) => new(merchantId);

    private readonly Guid? _merchantId;
    private FakeActorContext(Guid? merchantId) => _merchantId = merchantId;

    public Guid MerchantId => _merchantId ?? throw new InvalidOperationException("No actor bound.");
    public Guid? UserId => null;
    public bool HasActor => _merchantId.HasValue;
}
