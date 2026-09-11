namespace Payments.Application.Ports;

/// <summary>
/// A charge attempt that DEFINITIVELY created no charge: either the request never left us (an amount the
/// currency cannot express, a method the adapter does not drive, a secret it cannot use) or the PSP answered
/// with an outright refusal (a 4xx other than 408/429, a decline code). Only these may fail the session.
/// <para>Everything else an adapter can throw — timeout, cancellation, transport fault, 5xx, a response we
/// cannot read or cannot verify — is AMBIGUOUS: the PSP may be holding a charge we never heard about. Failing
/// the session there would let the order open a second session, whose id is a NEW idempotency key at the PSP,
/// which is a second charge (REQ-7.5). Such a session keeps its claim and settles it by calling again under
/// its original key instead (REQ-7.6).</para>
/// Derives from <see cref="InvalidOperationException"/> so it keeps mapping to 409: a refusal the server's
/// own state or configuration caused, not malformed client input.
/// </summary>
public sealed class PspRejectedException : InvalidOperationException
{
    public PspRejectedException(string message) : base(message) { }
}
