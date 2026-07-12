namespace BuildingBlocks.Application;

/// <summary>
/// A request conflicts with the current state of a resource (e.g. provisioning a tenant code that
/// already exists). Maps to HTTP 409. Distinct from <see cref="ConcurrencyConflictException"/>, which
/// signals an optimistic-concurrency race on an existing row.
/// </summary>
public sealed class ConflictException : Exception
{
    /// <summary>
    /// An OPTIONAL caller-safe reason surfaced verbatim as the 409 <c>detail</c>. It MUST be a fixed,
    /// caller-safe sentence — NEVER interpolate ids, emails, SQL text, or tenant state (those belong in
    /// <see cref="Exception.Message"/>, which is logged server-side only). When null, the handler falls
    /// back to the generic conflict detail, so an un-annotated throw site degrades safely.
    /// </summary>
    public string? SafeDetail { get; }

    public ConflictException(string message) : base(message) { }

    public ConflictException(string message, Exception innerException) : base(message, innerException) { }

    public ConflictException(string message, string? safeDetail) : base(message) => SafeDetail = safeDetail;

    public ConflictException(string message, string? safeDetail, Exception innerException)
        : base(message, innerException) => SafeDetail = safeDetail;
}
