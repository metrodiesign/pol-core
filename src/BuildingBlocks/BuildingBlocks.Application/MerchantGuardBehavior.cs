using Mediator;

namespace BuildingBlocks.Application;

/// <summary>
/// Pipeline behavior that rejects an <see cref="IMerchantScoped"/> message dispatched without an
/// actor in context. Depends on the Scoped <see cref="IActorContext"/>, so it must itself be
/// registered Scoped — the host's <c>ValidateScopes=true</c> + DI validation test enforce that
/// (PLAN decision #7).
/// </summary>
public sealed class MerchantGuardBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage
{
    private readonly IActorContext _actor;

    public MerchantGuardBehavior(IActorContext actor) => _actor = actor;

    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (message is IMerchantScoped && !_actor.HasActor)
        {
            // Security-floor violation: a merchant-scoped message with no actor means RLS scoping is absent.
            // Mapped to an opaque 500 by the host — never confirm/deny binding state to the caller.
            throw new MerchantBindingException(
                $"Message '{typeof(TMessage).Name}' is merchant-scoped but no actor is bound to the request.");
        }

        return next(message, cancellationToken);
    }
}
