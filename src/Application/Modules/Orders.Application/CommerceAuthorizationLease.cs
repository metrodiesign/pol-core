using BuildingBlocks.Application;

namespace Orders.Application;

/// <summary>Authenticated identity proof carried into the Commerce transaction. A null proof is the
/// intentional legacy ConsoleSession path; identity requests always carry the account version and the exact
/// permission or registered SystemClient scope that the endpoint admitted.</summary>
public sealed record CommerceAuthorizationProof(
    Guid AccountId,
    long ExpectedAuthorizationVersion,
    Guid MerchantId,
    string? SystemClientId,
    string? RequiredPermission,
    string? RequiredScope)
{
    public bool IsSystemClient => !string.IsNullOrWhiteSpace(SystemClientId);
}

/// <summary>Revalidates an identity proof while holding the Commerce transaction open. Implementations must
/// take the Account row lock before any replay, idempotency, aggregate, or outbox operation.</summary>
public interface ICommerceAuthorizationLease
{
    Task VerifyAsync(CommerceAuthorizationProof? proof, CancellationToken cancellationToken);
}

/// <summary>Direct application tests and legacy non-HTTP callers have no identity proof to revalidate. The
/// production Commerce registration replaces this with the SQL implementation.</summary>
public sealed class NoopCommerceAuthorizationLease : ICommerceAuthorizationLease
{
    public Task VerifyAsync(CommerceAuthorizationProof? proof, CancellationToken cancellationToken)
    {
        if (proof is null)
            return Task.CompletedTask;
        throw new AccessDeniedException(
            "Commerce authorization lease is not configured.", "authorization_lease_unavailable");
    }
}
