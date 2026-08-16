using SharedKernel;

namespace Admins.Domain.Users;

/// <summary>
/// A platform operator in the Admin Console. Control-plane — NOT under the merchant RLS predicate (REQ-3.2).
/// <see cref="Tier"/> decides reach: a <see cref="Tier.Super"/> has unrestricted cross-merchant control;
/// a <see cref="Tier.Scoped"/> admin reaches only its assigned merchants. <see cref="Subject"/> (Google
/// <c>sub</c>) is unique once bound, but is NULL for an invited Scoped account until its first login binds it
/// (REQ-3.1/3.5) — the unique <see cref="Email"/> is the invite key in the meantime. Admins are bootstrapped
/// (allowlist self-provision) or created by a Super, never <c>PendingApproval</c> (REQ-3.3).
/// </summary>
public sealed class User : AggregateRoot<Guid>
{
    /// <summary>Provider slugs. The admin module cannot reference <c>Merchants.Domain.Users.ExternalLogin</c>
    /// (module boundary), so the canonical slugs are mirrored here for the admin plane.</summary>
    public const string GoogleProvider = "google";
    public const string MicrosoftProvider = "microsoft";

    /// <summary>The identity provider slug ("google"/"microsoft") the <see cref="Subject"/> came from — identity is
    /// the PAIR <c>(Provider, Subject)</c>, never the subject alone (microsoft-oidc-ciam-alignment REQ-4.1).
    /// Defaults to "google" for rows created before the discriminator existed.</summary>
    public string Provider { get; private set; } = GoogleProvider;

    /// <summary>The provider's stable subject (Google <c>sub</c> / Entra <c>oid</c>). NULL until an invited Scoped
    /// account's first login binds it; unique per provider once set.</summary>
    public string? Subject { get; private set; }

    /// <summary>Verified email. Unique — the invite key before a <see cref="Subject"/> is bound.</summary>
    public string Email { get; private set; } = default!;

    public Tier Tier { get; private set; }

    public UserStatus Status { get; private set; }

    /// <summary>rls-to-query-filter REQ-4.11: bumped in the SAME transaction as every write that changes
    /// this admin's effective authorization (Status/Tier/Session/MerchantAccess/RoleAssignment/
    /// RolePermission) — a caller holding a stale <see cref="AuthorizationVersion"/> fails the in-tx
    /// authorization lease. Not yet a real column (task 8's migration adds it) — the migration-owner's own
    /// config explicitly ignores this property until then, so setting it today is a safe no-op there.</summary>
    public long AuthorizationVersion { get; private set; }

    /// <summary>Monotonic resource version for Admin Console ETag/If-Match concurrency. Separate from
    /// <see cref="AuthorizationVersion"/> so profile-only edits never invalidate live sessions.</summary>
    public long Version { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    /// <summary>ตำแหน่ง — FK to <see cref="Position"/>. NULL until set via invite or the profile edit.</summary>
    public Guid? PositionId { get; private set; }

    /// <summary>สถานที่ปฏิบัติงาน — FK to <see cref="Office"/>.</summary>
    public Guid? OfficeId { get; private set; }

    /// <summary>ระดับ — FK to <see cref="Level"/>.</summary>
    public Guid? LevelId { get; private set; }

    /// <summary>ฝ่าย/ภาค — FK to <see cref="Division"/>.</summary>
    public Guid? DivisionId { get; private set; }

    private User() { }

    private User(
        Guid id, string provider, string? subject, string email, Tier tier, DateTime createdAt,
        Guid? positionId, Guid? officeId, Guid? levelId, Guid? divisionId) : base(id)
    {
        Provider = provider;
        Subject = subject;
        Email = email;
        Tier = tier;
        Status = UserStatus.Active;
        CreatedAt = createdAt;
        PositionId = positionId;
        OfficeId = officeId;
        LevelId = levelId;
        DivisionId = divisionId;
        Version = 1;
    }

    /// <summary>The first Super Admin bootstrapping from the config allowlist on first login (REQ-5.1). The
    /// subject is bound immediately (the caller has authenticated).</summary>
    public static User SelfProvision(string provider, string subject, string email, DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new User(Guid.NewGuid(), provider.Trim(), subject.Trim(), email.Trim(), Tier.Super, createdAt,
            positionId: null, officeId: null, levelId: null, divisionId: null);
    }

    /// <summary>A Scoped admin invited by a Super (REQ-3.4): keyed by verified email, with an unbound
    /// <see cref="Subject"/> until the invitee's first login. Profile FKs are optional at invite time (an
    /// invited account may have no known position yet); the caller validates each FK exists and is active.</summary>
    public static User CreateScoped(
        string email, DateTime createdAt,
        Guid? positionId = null, Guid? officeId = null, Guid? levelId = null, Guid? divisionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new User(Guid.NewGuid(), provider: GoogleProvider, subject: null, email.Trim(), Tier.Scoped, createdAt,
            positionId, officeId, levelId, divisionId);
    }

    /// <summary>Binds the provider identity to an invited account on its first login (REQ-3.5). Idempotent
    /// re-binding is rejected — a bound account is resolved by (provider, subject), never re-bound.</summary>
    public void BindSubject(string provider, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (Subject is not null)
            throw new InvalidOperationException("This admin account already has a bound subject.");
        Provider = provider.Trim();
        Subject = subject.Trim();
        BumpResourceVersion();
    }

    /// <summary>Revokes access. <paramref name="actingAdminId"/> is the admin performing the suspension;
    /// suspending your OWN account is rejected so oversight can never be locked out (REQ-8.2).</summary>
    public void Suspend(Guid actingAdminId)
    {
        if (actingAdminId == Id)
            throw new InvalidOperationException("An admin cannot suspend their own account.");
        if (Status == UserStatus.Suspended)
            return;
        Status = UserStatus.Suspended;
        BumpAuthorizationVersion();
        BumpResourceVersion();
    }

    /// <summary>Restores access (admin-account-management REQ-3). Idempotent: an already-Active account stays
    /// Active. No self-guard is needed (a suspended admin cannot authenticate, so self-reactivation cannot
    /// arise). Revoking the target's sessions on the Suspended->Active transition is the caller's
    /// responsibility — the handler owns the transaction and the session store (REQ-3.5).</summary>
    public void Reactivate()
    {
        if (Status == UserStatus.Active)
            return;
        Status = UserStatus.Active;
        BumpAuthorizationVersion();
        BumpResourceVersion();
    }

    /// <summary>Promotes/demotes between Scoped and Super (rls-to-query-filter REQ-4.11 invalidation-matrix
    /// source "Tier"). Mirrors <see cref="Suspend"/>'s self-guard — an admin changing their OWN tier could
    /// strand oversight (a lone Super demoting itself with no other Super left) exactly like self-suspend
    /// could, so it is rejected the same way. Idempotent: setting the current tier is a no-op (no spurious
    /// version bump).</summary>
    public void ChangeTier(Tier newTier, Guid actingAdminId)
    {
        if (actingAdminId == Id)
            throw new InvalidOperationException("An admin cannot change their own tier.");
        if (Tier == newTier)
            return;
        Tier = newTier;
        BumpAuthorizationVersion();
        BumpResourceVersion();
    }

    /// <summary>Invalidates every authorization lease this admin currently holds. Called directly for
    /// mutations local to this aggregate (<see cref="Suspend"/>/<see cref="Reactivate"/>/
    /// <see cref="ChangeTier"/>); a caller mutating a RELATED aggregate that also affects this admin's
    /// effective authorization (MerchantAccess grant/revoke, RoleAssignment add/remove, RolePermission
    /// update/delete) calls this explicitly on the loaded admin in the same transaction.</summary>
    public void BumpAuthorizationVersion() => AuthorizationVersion++;

    public void BumpResourceVersion() => Version++;

    /// <summary>Full replace of the org-profile FKs (a NULL clears that dimension). The caller validates that
    /// each non-null FK references an existing, active master before calling — the aggregate only stores ids.</summary>
    public void UpdateProfile(Guid? positionId, Guid? officeId, Guid? levelId, Guid? divisionId)
    {
        if (PositionId == positionId && OfficeId == officeId && LevelId == levelId && DivisionId == divisionId)
            return;
        PositionId = positionId;
        OfficeId = officeId;
        LevelId = levelId;
        DivisionId = divisionId;
        BumpResourceVersion();
    }
}
