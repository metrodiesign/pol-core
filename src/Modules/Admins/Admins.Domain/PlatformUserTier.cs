namespace Admins.Domain;

/// <summary>Admin privilege level (admin-actor-rename REQ-3). <see cref="Super"/> has unrestricted
/// cross-merchant control; <see cref="Scoped"/> reaches only the merchants assigned to it
/// (<see cref="PlatformMerchantAccess"/>).</summary>
public enum PlatformUserTier
{
    Scoped = 0,
    Super = 1,
}
