namespace BuildingBlocks.Application;

/// <summary>
/// A request conflicts with the current state of a resource (e.g. provisioning a merchant code that
/// already exists). Maps to HTTP 409. Distinct from <see cref="ConcurrencyConflictException"/>, which
/// signals an optimistic-concurrency race on an existing row.
/// </summary>
public sealed class ConflictException : Exception
{
    /// <summary>Optional stable, caller-safe machine code for a known conflict.</summary>
    public string? Code { get; }

    public ConflictException(string message) : base(message) { }

    public ConflictException(string message, Exception innerException) : base(message, innerException) { }

    public ConflictException(string message, string code) : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Conflict code is required.", nameof(code))
            : code;
    }

    public ConflictException(string message, string code, Exception innerException) : base(message, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Conflict code is required.", nameof(code))
            : code;
    }
}
