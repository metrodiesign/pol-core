using SharedKernel;

namespace Merchants.Domain.Users.Roles;

/// <summary>Links a <see cref="User"/> to a <see cref="Role"/> within the merchant it was
/// approved into — the home of a merchant-user's role(s), which are NOT a column on <see cref="User"/>. Standalone
/// child row with a surrogate id and a unique (MerchantUserId, RoleId); <see cref="MerchantId"/> scopes the grant to the
/// approved merchant and <see cref="AssignedByAdminId"/> records the approving admin. There is no per-assignment status
/// column: the effective-permission union keys on the ROLE's status, mirroring
/// <see cref="Admins.Domain.Roles.RoleAssignment"/>.</summary>
// ponytail: DUPLICATE of Admins.Domain.Roles.RoleAssignment (+ MerchantId rename) — deliberate debt, do not refactor into a shared base.
public sealed class RoleAssignment : Entity<Guid>
{
    public Guid MerchantUserId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid AssignedByAdminId { get; private set; }
    public DateTime AssignedAt { get; private set; }

    private RoleAssignment() { }

    private RoleAssignment(Guid id, Guid merchantUserId, Guid roleId, Guid merchantId, Guid assignedByAdminId,
        DateTime assignedAt) : base(id)
    {
        MerchantUserId = merchantUserId;
        RoleId = roleId;
        MerchantId = merchantId;
        AssignedByAdminId = assignedByAdminId;
        AssignedAt = assignedAt;
    }

    public static RoleAssignment Create(Guid merchantUserId, Guid roleId, Guid merchantId, Guid assignedByAdminId,
        DateTime assignedAt)
    {
        if (merchantUserId == Guid.Empty)
            throw new ArgumentException("MerchantUserId is required.", nameof(merchantUserId));
        if (roleId == Guid.Empty)
            throw new ArgumentException("RoleId is required.", nameof(roleId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        return new RoleAssignment(Guid.NewGuid(), merchantUserId, roleId, merchantId, assignedByAdminId, assignedAt);
    }
}
