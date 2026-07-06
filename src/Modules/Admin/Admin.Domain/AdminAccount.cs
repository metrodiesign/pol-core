using SharedKernel;

namespace Admin.Domain;

/// <summary>
/// A platform operator in the Admin Console. Control-plane — NOT under the tenant RLS predicate (REQ-3.2).
/// <see cref="Tier"/> decides reach: a <see cref="AdminTier.Super"/> has unrestricted cross-tenant control;
/// a <see cref="AdminTier.Scoped"/> admin reaches only its assigned tenants. <see cref="Subject"/> (Google
/// <c>sub</c>) is unique once bound, but is NULL for an invited Scoped account until its first login binds it
/// (REQ-3.1/3.5) — the unique <see cref="Email"/> is the invite key in the meantime. Admins are bootstrapped
/// (allowlist self-provision) or created by a Super, never <c>PendingApproval</c> (REQ-3.3).
/// </summary>
public sealed class AdminAccount : AggregateRoot<Guid>
{
    /// <summary>Stable Google subject. NULL until an invited Scoped account's first login binds it; unique once set.</summary>
    public string? Subject { get; private set; }

    /// <summary>Verified email. Unique — the invite key before a <see cref="Subject"/> is bound.</summary>
    public string Email { get; private set; } = default!;

    public AdminTier Tier { get; private set; }

    public AdminStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private AdminAccount() { }

    private AdminAccount(Guid id, string? subject, string email, AdminTier tier, DateTime createdAt) : base(id)
    {
        Subject = subject;
        Email = email;
        Tier = tier;
        Status = AdminStatus.Active;
        CreatedAt = createdAt;
    }

    /// <summary>The first Super Admin bootstrapping from the config allowlist on first login (REQ-5.1). The
    /// subject is bound immediately (the caller has authenticated).</summary>
    public static AdminAccount SelfProvision(string subject, string email, DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new AdminAccount(Guid.NewGuid(), subject.Trim(), email.Trim(), AdminTier.Super, createdAt);
    }

    /// <summary>A Scoped admin invited by a Super (REQ-3.4): keyed by verified email, with an unbound
    /// <see cref="Subject"/> until the invitee's first login.</summary>
    public static AdminAccount CreateScoped(string email, DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new AdminAccount(Guid.NewGuid(), subject: null, email.Trim(), AdminTier.Scoped, createdAt);
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
}
