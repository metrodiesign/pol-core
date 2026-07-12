namespace Merchants.Domain.Users.Roles;

/// <summary>Whether a role contributes its permissions. An Inactive role is excluded from a merchant-user's effective
/// permission union. Stored as int (mirrors <see cref="UserStatus"/>).</summary>
// ponytail: DUPLICATE of Admins.Domain.AdminRoleStatus — deliberate debt, do not refactor into a shared base.
public enum RoleStatus
{
    Active = 0,
    Inactive = 1,
}
