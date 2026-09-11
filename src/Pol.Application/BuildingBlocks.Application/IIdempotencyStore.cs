namespace BuildingBlocks.Application;

/// <summary>
/// Multi-key idempotency ledger for the payment / webhook path (PLAN decision #9). A single
/// logical event registers several keys at once — e.g. <c>(psp,eventId)</c> AND
/// <c>(psp,externalChargeId,normalizedStatus)</c> — so a PSP replaying with a different event id
/// for the same charge is still caught. Registration is atomic: either all keys are newly
/// claimed (first delivery) or the call reports a duplicate.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims every key for <paramref name="context"/>. Returns <c>true</c> on first
    /// delivery (all keys were new), <c>false</c> if any key was already seen (replay/duplicate).
    /// </summary>
    Task<bool> TryBeginAsync(IReadOnlyCollection<string> keys, string context, CancellationToken cancellationToken);
}
