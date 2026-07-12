using Merchants.Application.Users;

namespace Merchants.Application;

/// <summary>
/// Per-request holder of the resolved merchant user (REQ-17.1). The merchant-user session authentication handler calls
/// <c>Set</c> once per request; readers consume this seam. Fail-closed: a request that authenticates via no scheme
/// binds no merchant user, leaving <see cref="IsBound"/> false — so
/// <c>RequireMerchantUserPermission</c> denies it 403 even though authentication passed (REQ-17.2/F10).
/// </summary>
public interface IUserScope
{
    bool IsBound { get; }
    Resolution Current { get; }
}
