namespace Iam.Domain.Roles;

/// <summary>Whether a role contributes its permissions. An Inactive role is excluded from an actor's effective
/// permission union (REQ-4.6). Stored as int.</summary>
public enum RoleStatus
{
    Active = 1,
    Inactive = 2,
}
