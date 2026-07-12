using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Scoped holder for an explicitly-bound actor (see <see cref="IActorScope"/>). A host's
/// <see cref="IActorContext"/> consults this first, so a webhook or dispatcher scope that calls
/// <see cref="Begin"/> drives the RLS <c>SESSION_CONTEXT</c> exactly like an authenticated request.
/// Registered Scoped — one binding per unit of work; the binding is cleared when its handle disposes.
/// </summary>
public sealed class AmbientActor : IActorScope
{
    private Guid? _merchantId;
    private Guid? _userId;

    public bool IsBound => _merchantId.HasValue;

    public Guid MerchantId =>
        _merchantId ?? throw new InvalidOperationException("No ambient actor is bound.");

    public Guid? UserId => _userId;

    public IDisposable Begin(Guid merchantId, Guid? userId = null)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("Merchant id cannot be empty.", nameof(merchantId));
        if (_merchantId.HasValue)
            throw new InvalidOperationException(
                "An actor is already bound to this scope; nested or re-entrant binding is not allowed.");

        _merchantId = merchantId;
        _userId = userId;
        return new Handle(this);
    }

    private void Clear()
    {
        _merchantId = null;
        _userId = null;
    }

    private sealed class Handle : IDisposable
    {
        private AmbientActor? _owner;
        public Handle(AmbientActor owner) => _owner = owner;

        public void Dispose()
        {
            _owner?.Clear();
            _owner = null;
        }
    }
}
