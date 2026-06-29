namespace Producer.Domain;

/// <summary>Whether a role contributes its permissions. An Inactive role is excluded from a producer's effective
/// permission union (REQ-16.4). Stored as int (mirrors <see cref="ProducerAccountStatus"/>).</summary>
// ponytail: DUPLICATE of Admin.Domain.AdminRoleStatus — deliberate debt, do not refactor into a shared base.
public enum ProducerRoleStatus
{
    Active = 0,
    Inactive = 1,
}
