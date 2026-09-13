namespace Merchants.Domain;

/// <summary>
/// Lifecycle state of a <see cref="Merchant"/>. In this scope provisioning sets <see cref="Active"/>
/// directly (single transaction, no maker-checker).
/// </summary>
public enum MerchantStatus
{
    Active = 1,
    Inactive = 2,
}
