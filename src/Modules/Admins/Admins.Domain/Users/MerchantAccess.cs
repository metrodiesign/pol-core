using SharedKernel;

namespace Admins.Domain.Users;

/// <summary>An edge granting a Scoped <see cref="User"/> reach over one merchant (REQ-4). Control-plane;
/// <see cref="MerchantId"/> is a SOFT reference to a Merchant (no DB FK — Admin does not reference the Merchants
/// module; existence/active is validated via the host's <c>IAdminMerchantDirectory</c> at assignment time). This
/// table is the RLS predicate's lookup table (<c>sec.fn_merchant_predicate</c> platform branch, REQ-3.2/3.9).
/// Uniqueness on <c>(AdminUserId, MerchantId)</c> is enforced by the DB index; unassign is a hard delete.</summary>
public sealed class MerchantAccess : Entity<Guid>
{
    public Guid AdminUserId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid AssignedByAdminId { get; private set; }
    public DateTime AssignedAt { get; private set; }

    private MerchantAccess() { }

    private MerchantAccess(Guid id, Guid platformUserId, Guid merchantId, Guid assignedByAdminId, DateTime assignedAt)
        : base(id)
    {
        AdminUserId = platformUserId;
        MerchantId = merchantId;
        AssignedByAdminId = assignedByAdminId;
        AssignedAt = assignedAt;
    }

    public static MerchantAccess Create(Guid platformUserId, Guid merchantId, Guid assignedByAdminId, DateTime assignedAt)
    {
        if (platformUserId == Guid.Empty)
            throw new ArgumentException("AdminUserId is required.", nameof(platformUserId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        return new MerchantAccess(Guid.NewGuid(), platformUserId, merchantId, assignedByAdminId, assignedAt);
    }
}
