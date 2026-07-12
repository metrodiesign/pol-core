namespace BuildingBlocks.Application;

/// <summary>
/// Raised when an <see cref="IMerchantScoped"/> message reaches the pipeline with no actor bound to the
/// request (and no admin override) — a security-floor violation, because RLS scoping would be absent.
/// The host's ProblemDetails handler maps this to an OPAQUE 500: whether an actor is bound must never be
/// confirmed or denied to a caller. Distinct from <see cref="InvalidOperationException"/> for exactly that
/// mapping.
/// </summary>
public sealed class MerchantBindingException : Exception
{
    public MerchantBindingException(string message) : base(message) { }
}
