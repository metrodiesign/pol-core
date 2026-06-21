namespace BuildingBlocks.Application;

/// <summary>
/// Raised by <see cref="IUnitOfWork"/> when an optimistic-concurrency token check fails on save —
/// another request mutated the same aggregate first. Application handlers catch THIS (not the
/// provider-specific EF exception) so they stay infrastructure-free.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message) : base(message) { }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException) { }
}
