using System.Linq.Expressions;
using Iam.Domain.Permissions;

namespace Iam.Domain.Roles;

/// <summary>
/// The role rows a side can see (REQ-3.6/3.9): Platform sees exactly the Platform+shared (NULL
/// <c>MerchantId</c>) rows; Merchant sees Merchant+shared rows plus its own <c>MerchantId</c>'s custom rows.
/// Defined ONCE, in the published-language module, so every reader — <c>Iam.Infrastructure</c>'s own store
/// AND each side's assignment-support repository (<c>Admins</c>/<c>Merchants.Infrastructure</c>, which query
/// <c>iam.Roles</c> directly for assignment validation) — applies the identical rule instead of re-deriving
/// it. Re-deriving this predicate per module is exactly the kind of duplicated invariant rf2 exists to kill.
/// </summary>
public static class RoleVisibility
{
    public static Expression<Func<Role, bool>> For(Scope scope, Guid? merchantId) => scope switch
    {
        Scope.Platform => r => r.Scope == Scope.Platform && r.MerchantId == null,
        Scope.Merchant => r => r.Scope == Scope.Merchant && (r.MerchantId == null || r.MerchantId == merchantId),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Scope."),
    };
}
