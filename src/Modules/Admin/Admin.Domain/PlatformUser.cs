using SharedKernel;

namespace Admin.Domain;

/// <summary>
/// A platform operator in the Admin Console. Control-plane — NOT under the merchant RLS predicate (REQ-3.2).
/// <see cref="Tier"/> decides reach: a <see cref="PlatformUserTier.Super"/> has unrestricted cross-merchant control;
/// a <see cref="PlatformUserTier.Scoped"/> admin reaches only its assigned merchants. <see cref="Subject"/> (Google
/// <c>sub</c>) is unique once bound, but is NULL for an invited Scoped account until its first login binds it
/// (REQ-3.1/3.5) — the unique <see cref="Email"/> is the invite key in the meantime. Admins are bootstrapped
/// (allowlist self-provision) or created by a Super, never <c>PendingApproval</c> (REQ-3.3).
/// </summary>
public sealed class PlatformUser : AggregateRoot<Guid>
{
    /// <summary>Stable Google subject. NULL until an invited Scoped account's first login binds it; unique once set.</summary>
    public string? Subject { get; private set; }

    /// <summary>Verified email. Unique — the invite key before a <see cref="Subject"/> is bound.</summary>
    public string Email { get; private set; } = default!;

    public PlatformUserTier Tier { get; private set; }

    public AdminStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>ตำแหน่ง — FK to <see cref="Position"/>. NULL until set via invite or the profile edit.</summary>
    public Guid? PositionId { get; private set; }

    /// <summary>สถานที่ปฏิบัติงาน — FK to <see cref="Office"/>.</summary>
    public Guid? OfficeId { get; private set; }

    /// <summary>ระดับ — FK to <see cref="Level"/>.</summary>
    public Guid? LevelId { get; private set; }

    /// <summary>ฝ่าย/ภาค — FK to <see cref="Division"/>.</summary>
    public Guid? DivisionId { get; private set; }

    private PlatformUser() { }

    private PlatformUser(
        Guid id, string? subject, string email, PlatformUserTier tier, DateTime createdAt,
        Guid? positionId, Guid? officeId, Guid? levelId, Guid? divisionId) : base(id)
    {
        Subject = subject;
        Email = email;
        Tier = tier;
        Status = AdminStatus.Active;
        CreatedAt = createdAt;
        PositionId = positionId;
        OfficeId = officeId;
        LevelId = levelId;
        DivisionId = divisionId;
    }

    /// <summary>The first Super Admin bootstrapping from the config allowlist on first login (REQ-5.1). The
    /// subject is bound immediately (the caller has authenticated).</summary>
    public static PlatformUser SelfProvision(string subject, string email, DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new PlatformUser(Guid.NewGuid(), subject.Trim(), email.Trim(), PlatformUserTier.Super, createdAt,
            positionId: null, officeId: null, levelId: null, divisionId: null);
    }

    /// <summary>A Scoped admin invited by a Super (REQ-3.4): keyed by verified email, with an unbound
    /// <see cref="Subject"/> until the invitee's first login. Profile FKs are optional at invite time (an
    /// invited account may have no known position yet); the caller validates each FK exists and is active.</summary>
    public static PlatformUser CreateScoped(
        string email, DateTime createdAt,
        Guid? positionId = null, Guid? officeId = null, Guid? levelId = null, Guid? divisionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new PlatformUser(Guid.NewGuid(), subject: null, email.Trim(), PlatformUserTier.Scoped, createdAt,
            positionId, officeId, levelId, divisionId);
    }

    /// <summary>Binds the Google subject to an invited account on its first login (REQ-3.5). Idempotent
    /// re-binding is rejected — a bound account is resolved by subject, never re-bound.</summary>
    public void BindSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (Subject is not null)
            throw new InvalidOperationException("This admin account already has a bound subject.");
        Subject = subject.Trim();
    }

    /// <summary>Revokes access. <paramref name="actingAdminId"/> is the admin performing the suspension;
    /// suspending your OWN account is rejected so oversight can never be locked out (REQ-8.2).</summary>
    public void Suspend(Guid actingAdminId)
    {
        if (actingAdminId == Id)
            throw new InvalidOperationException("An admin cannot suspend their own account.");
        Status = AdminStatus.Suspended;
    }

    /// <summary>Restores access (admin-account-management REQ-3). Idempotent: an already-Active account stays
    /// Active. No self-guard is needed (a suspended admin cannot authenticate, so self-reactivation cannot
    /// arise). Revoking the target's sessions on the Suspended->Active transition is the caller's
    /// responsibility — the handler owns the transaction and the session store (REQ-3.5).</summary>
    public void Reactivate() => Status = AdminStatus.Active;

    /// <summary>Full replace of the org-profile FKs (a NULL clears that dimension). The caller validates that
    /// each non-null FK references an existing, active master before calling — the aggregate only stores ids.</summary>
    public void UpdateProfile(Guid? positionId, Guid? officeId, Guid? levelId, Guid? divisionId)
    {
        PositionId = positionId;
        OfficeId = officeId;
        LevelId = levelId;
        DivisionId = divisionId;
    }
}
