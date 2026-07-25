namespace Orders.Domain.Items;

/// <summary>Whether an <see cref="ItemPolicyAudit"/> row records the first write to an
/// <see cref="ItemPolicy"/> or a later edit (REQ-3.5).</summary>
public enum AuditOperation
{
    Created = 0,
    Updated = 1,
}
