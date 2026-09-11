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
    private readonly ISecurityTelemetry _telemetry;

    public MerchantGuardBehavior(IActorContext actor, ISecurityTelemetry telemetry)
    {
        _actor = actor;
        _telemetry = telemetry;
    }

    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (message is IMerchantScoped && !_actor.HasActor)
        {
            // Security-floor violation: a merchant-scoped message with no actor means RLS scoping is absent.
            // Mapped to an opaque 500 by the host — never confirm/deny binding state to the caller.
            _telemetry.Emit(new DenialEvent(
                DenialCategory.UnboundActor, "request", ActorId: null, TargetMerchant: null,
                typeof(TMessage).Name, "Dispatch", "IMerchantScoped message dispatched with no actor bound.",
                CorrelationId.Current, DateTime.UtcNow));
            throw new MerchantBindingException(
                $"Message '{typeof(TMessage).Name}' is merchant-scoped but no actor is bound to the request.");
        }

        return next(message, cancellationToken);
    }
}
