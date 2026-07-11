namespace BuildingBlocks.Application;

/// <summary>
/// A request conflicts with the current state of a resource (e.g. provisioning a merchant code that
/// already exists). Maps to HTTP 409. Distinct from <see cref="ConcurrencyConflictException"/>, which
/// signals an optimistic-concurrency race on an existing row.
/// </summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }

    public ConflictException(string message, Exception innerException) : base(message, innerException) { }
}
