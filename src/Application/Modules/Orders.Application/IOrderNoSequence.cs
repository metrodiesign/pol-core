namespace Orders.Application;

/// <summary>
/// Mints the next human-readable order number (purchase-flow-completion REQ-7.1): <c>ORD</c> + the 2-digit
/// Buddhist year + an 8-digit running number, e.g. <c>ORD6900000006</c>. The running part comes from ONE
/// platform-wide SQL sequence that never resets — the year is a display prefix taken from the minting date,
/// not part of the key, so there is no cross-year race and no per-year counter to reconcile.
/// <para>
/// A number consumed by a call that later rolls back is simply lost (a sequence is not transactional); gaps
/// are accepted, duplicates are not — which is the trade this port exists to make explicit.
/// </para>
/// </summary>
public interface IOrderNoSequence
{
    Task<string> NextAsync(CancellationToken cancellationToken);
}
