namespace BuildingBlocks.Application;

/// <summary>
/// Raised when a resource existed but is no longer available — a capability that has expired, such as an
/// order summary link past its TTL. A typed exception so the HTTP layer maps it to 410 Gone distinctly from
/// a 404 (the resource was real) and a 409. Messages carry only caller-supplied identifiers; the host's
/// ProblemDetails handler emits a generic 410 body.
/// </summary>
public sealed class GoneException : Exception
{
    public GoneException(string message) : base(message) { }
}
