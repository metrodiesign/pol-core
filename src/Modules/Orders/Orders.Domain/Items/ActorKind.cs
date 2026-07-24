namespace Orders.Domain.Items;

/// <summary>Which plane wrote an <see cref="ItemPolicyAudit"/> row — admin (cross-merchant) or the merchant's
/// own producer/staff (REQ-3.2). Distinct from <see cref="RevealAudit.ActorType"/>'s lowercase string
/// vocabulary (design.md's own naming for this field), not a reuse of it.</summary>
public enum ActorKind
{
    Admin = 0,
    MerchantUser = 1,
}
