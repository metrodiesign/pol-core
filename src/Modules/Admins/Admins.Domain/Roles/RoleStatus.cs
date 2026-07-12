namespace Admins.Domain.Roles;

/// <summary>Whether a role contributes its permissions. An Inactive role is excluded from an admin's effective
/// permission union (REQ-5.2). Stored as int (mirrors <see cref="UserStatus"/>).</summary>
public enum RoleStatus
{
    Active = 0,
    Inactive = 1,
}
