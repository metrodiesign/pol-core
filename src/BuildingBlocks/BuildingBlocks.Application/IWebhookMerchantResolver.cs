namespace BuildingBlocks.Application;

/// <summary>
/// Maps a trusted PSP connection id to its owning merchant for the unauthenticated webhook path.
/// The webhook has no session claim yet RLS hides every <c>PspConnections</c> row until a merchant is
/// bound — a chicken-and-egg. The implementation resolves the id through a stored procedure that runs
/// as a bypass principal, so it can read ONLY the one mapping row and return ONLY the merchant id; a
/// merchant principal still cannot read connections directly. The caller then binds that merchant
/// (<see cref="IActorScope"/>) before sending the webhook command, so all further work is RLS-scoped.
/// </summary>
public interface IWebhookMerchantResolver
{
    /// <summary>Returns the connection's merchant, or null if no such connection exists.</summary>
    Task<Guid?> ResolveMerchantAsync(Guid pspConnectionId, CancellationToken cancellationToken);
}
