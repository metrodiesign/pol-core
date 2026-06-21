using Mediator;

namespace BuildingBlocks.Application;

/// <summary>
/// Pipeline behavior that rejects an <see cref="ITenantScoped"/> message dispatched without a
/// tenant in context (and without an admin cross-tenant override). Depends on the Scoped
/// <see cref="ITenantContext"/>, so it must itself be registered Scoped — the host's
/// <c>ValidateScopes=true</c> + DI validation test enforce that (PLAN decision #7).
/// </summary>
public sealed class TenantGuardBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage
{
    private readonly ITenantContext _tenant;

    public TenantGuardBehavior(ITenantContext tenant) => _tenant = tenant;

    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (message is ITenantScoped && !_tenant.IsAdmin && !_tenant.HasTenant)
        {
            // Security-floor violation: a tenant-scoped message with no tenant means RLS scoping is absent.
            // Mapped to an opaque 500 by the host — never confirm/deny binding state to the caller.
            throw new TenantBindingException(
                $"Message '{typeof(TMessage).Name}' is tenant-scoped but no tenant is bound to the request.");
        }

        return next(message, cancellationToken);
    }
}
