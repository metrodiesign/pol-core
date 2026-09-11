namespace BuildingBlocks.Application;

/// <summary>
/// Raised by an application handler when a requested aggregate does not exist — or is invisible to the
/// caller under row-level security. A typed exception (not an overloaded <see cref="InvalidOperationException"/>)
/// so the HTTP layer can map "not found" to 404 distinctly from an illegal-state 409. Messages carry only
/// caller-supplied identifiers; the host's ProblemDetails handler still emits a generic 404 body.
/// </summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message, string? code = null) : base(message) => Code = code;

    public string? Code { get; }
}
