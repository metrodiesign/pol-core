namespace Payments.Application.Ports;

/// <summary>
/// A PSP interaction that ended UNDECIDABLE: a 5xx/408/429 that outlived the retries, or a response we could
/// not read or could not signature-verify. The PSP may be holding money we have not heard about, so no caller
/// may read this as "not paid" — the confirm surfaces answer pending (REQ-8.7), release refuses (409), and a
/// redirect claim survives to be settled under its original key (REQ-7.5/7.6).
/// <para>The named counterpart of <see cref="PspRejectedException"/> (provably no charge): the adapters
/// classify every failure as one of the two, so callers branch on TYPE instead of enumerating transport
/// exceptions — an enumeration is exactly what let a signature failure fall through as a definitive 409
/// (review PR #168). Transport faults the runtime itself throws (<see cref="HttpRequestException"/>,
/// <see cref="TaskCanceledException"/>) stay uncaught here and remain ambiguous by the same rule.</para>
/// Derives from <see cref="InvalidOperationException"/> so an uncatching surface still maps it to 409,
/// exactly as before the type existed.
/// </summary>
public sealed class PspAmbiguousException : InvalidOperationException
{
    public PspAmbiguousException(string message) : base(message) { }
}
