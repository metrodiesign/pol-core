namespace Merchants.Domain;

/// <summary>
/// Lifecycle state of a <see cref="Merchant"/>. In this scope provisioning sets <see cref="Active"/>
/// directly (single transaction, no maker-checker); suspend / pending-provisioning states are added
/// with the features that produce them (YAGNI).
/// </summary>
public enum MerchantStatus
{
    Active = 0,
}
