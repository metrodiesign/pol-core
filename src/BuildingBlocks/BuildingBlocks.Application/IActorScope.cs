namespace BuildingBlocks.Application;

/// <summary>
/// Binds an actor explicitly for entry points that do NOT carry an authenticated session claim:
/// the PSP webhook (merchant resolved from the connection id), the outbox dispatcher (merchant taken
/// from the message), and an AdminSession owner-local branch after explicit merchant-scope authorization.
/// <para>
/// The binding feeds <see cref="IActorContext"/>, so it must be set BEFORE the unit of work opens
/// its first connection (the RLS interceptor stamps <c>SESSION_CONTEXT</c> at open).
/// <see cref="Begin"/> throws if an actor is already bound (a confused-deputy guard) and the returned
/// handle clears the binding on dispose.
/// </para>
/// </summary>
public interface IActorScope
{
    IDisposable Begin(Guid merchantId, Guid? userId = null);
}
