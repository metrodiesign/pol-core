namespace Products.Application.Ports;

/// <summary>
/// The upstream search procedure refused the input it was given: one of the documented §6 errors,
/// 50001-50009 (invalid document type, product group, count mode, payment status, a blank branch or sale
/// code, or a reversed date range). The request was rejected before any data was read, so the caller may
/// correct it and retry.
/// <para>Derives from <see cref="ArgumentException"/> so the shared ProblemDetails handler maps it to 400
/// through the bucket it already has — no arm of its own. That handler emits a FIXED detail, so
/// <see cref="Exception.Message"/> never reaches the client: it exists for server logs and tests, and
/// <see cref="SpErrorNumber"/> is the machine-readable part.</para>
/// <para>Nearly every rejection is unreachable in practice because <c>ProductFilterDto</c> validates the
/// same rules at the HTTP boundary and cites the same numbers. Reaching one here means the two rule sets
/// have drifted apart, which is worth seeing in the logs.</para>
/// </summary>
public sealed class SpDocumentSearchRejectedException : ArgumentException
{
    public SpDocumentSearchRejectedException(int spErrorNumber, string message) : base(message) =>
        SpErrorNumber = spErrorNumber;

    /// <summary>The §6 error number the procedure threw, 50001-50009.</summary>
    public int SpErrorNumber { get; }
}
