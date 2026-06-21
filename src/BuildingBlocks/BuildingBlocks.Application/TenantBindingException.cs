namespace BuildingBlocks.Application;

/// <summary>
/// Raised when an <see cref="ITenantScoped"/> message reaches the pipeline with no tenant bound to the
/// request (and no admin override) — a security-floor violation, because RLS scoping would be absent.
/// The host's ProblemDetails handler maps this to an OPAQUE 500: whether a tenant is bound must never be
/// confirmed or denied to a caller. Distinct from <see cref="InvalidOperationException"/> for exactly that
/// mapping.
/// </summary>
public sealed class TenantBindingException : Exception
{
    public TenantBindingException(string message) : base(message) { }
}
