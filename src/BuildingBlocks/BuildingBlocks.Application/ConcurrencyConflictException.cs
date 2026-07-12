namespace BuildingBlocks.Application;

/// <summary>
/// Raised by <see cref="IUnitOfWork"/> when an optimistic-concurrency token check fails on save —
/// another request mutated the same aggregate first. Application handlers catch THIS (not the
/// provider-specific EF exception) so they stay infrastructure-free.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>
    /// An OPTIONAL caller-safe reason surfaced verbatim as the 409 <c>detail</c> (same contract as
    /// <see cref="ConflictException.SafeDetail"/>). Null falls back to the generic concurrency detail.
    /// </summary>
    public string? SafeDetail { get; }

    public ConcurrencyConflictException(string message) : base(message) { }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException) { }

    public ConcurrencyConflictException(string message, string? safeDetail, Exception innerException)
        : base(message, innerException) => SafeDetail = safeDetail;
}
