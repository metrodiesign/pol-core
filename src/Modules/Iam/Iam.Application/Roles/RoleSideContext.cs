using Iam.Domain.Permissions;

namespace Iam.Application.Roles;

/// <summary>
/// Which side a role-management operation runs as (REQ-3.4/3.5/3.7/3.9): <c>Platform</c> with
/// <c>MerchantId = null</c> for the admin console, <c>Merchant</c> with the caller's own <c>MerchantId</c> for
/// the merchant-user console. Every <c>Iam.Application</c> role command/query takes this as an explicit
/// parameter rather than deriving it internally — a client can never smuggle a side selection through the
/// request body (design.md "Scope บน wire: ไม่ expose"). The host composes it at ONE derivation point per
/// request (design.md "ห้าม endpoint ประกอบ record เองต่อ call site") since building it requires seeing both
/// <c>IAdminScope</c> and <c>IUserScope</c> — peer-module types <c>Iam.Application</c> must not reference.
/// </summary>
public sealed record RoleSideContext(Scope Scope, Guid? MerchantId)
{
    public static RoleSideContext Platform() => new(Scope.Platform, null);

    public static RoleSideContext Merchant(Guid merchantId) => new(Scope.Merchant, merchantId);
}
