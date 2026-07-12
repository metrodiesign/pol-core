namespace Merchants.Application;

/// <summary>
/// An ACTIVE merchant-user's identity + reach, materialized once per request into <see cref="IMerchantUserScope"/> by the
/// session authentication handler (REQ-17.1). The merchant + effective permissions are resolved fresh from the record,
/// NEVER from a token claim. Drives the ambient actor context (the <c>HttpActorContext</c> path) and the
/// <c>RequireMerchantUserPermission</c> gate.
/// </summary>
public sealed record MerchantUserResolution(Guid MerchantUserId, string Email, Guid MerchantId, IReadOnlySet<string> Permissions);

/// <summary>
/// Per-request holder of the resolved merchant user (REQ-17.1). The merchant-user session authentication handler calls
/// <c>Set</c> once per request; readers consume this seam. Fail-closed: a request that authenticates via no scheme
/// binds no merchant user, leaving <see cref="IsBound"/> false — so
/// <c>RequireMerchantUserPermission</c> denies it 403 even though authentication passed (REQ-17.2/F10).
/// </summary>
public interface IMerchantUserScope
{
    bool IsBound { get; }
    MerchantUserResolution Current { get; }
}
