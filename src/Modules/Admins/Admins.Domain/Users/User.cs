using SharedKernel;

namespace Admins.Domain.Users;

/// <summary>Observable result of applying the three-field employee profile.</summary>
public readonly record struct EmployeeProfileChange(
    bool Changed,
    bool EmployeeBound,
    bool EmployeeIdChanged,
    bool NamesChanged);

/// <summary>
/// A platform operator in the Admin Console. Control-plane — NOT under the merchant RLS predicate (REQ-3.2).
/// <see cref="Tier"/> decides reach: a <see cref="Tier.Super"/> has unrestricted cross-merchant control;
/// a <see cref="Tier.Scoped"/> admin reaches only its assigned merchants. Microsoft authentication identity is
/// the immutable tenant-aware tuple <c>(Provider, TenantId, Subject)</c>; <see cref="Email"/> is optional contact
/// data and never an ownership key. Admins are bootstrapped or created by a Super, never <c>PendingApproval</c>.
/// </summary>
public sealed class User : AggregateRoot<Guid>
{
    /// <summary>Provider slugs. The admin module cannot reference <c>Merchants.Domain.Users.ExternalLogin</c>
    /// (module boundary), so the canonical slugs are mirrored here for the admin plane.</summary>
    public const string GoogleProvider = "google";
    public const string MicrosoftProvider = "microsoft";

    /// <summary>The canonical identity-provider slug. Microsoft uses the exact lowercase value
    /// <see cref="MicrosoftProvider"/>.</summary>
    public string Provider { get; private set; } = GoogleProvider;

    /// <summary>The immutable workforce tenant for Microsoft identity; null for historical non-Microsoft identity.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>Provider subject: canonical Entra object ID for Microsoft or the historical provider subject.</summary>
    public string? Subject { get; private set; }

    /// <summary>Optional contact data. It is not unique and is never consulted for authentication ownership.</summary>
    public string? Email { get; private set; }

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

    /// <summary>Normalised Graph <c>employeeId</c> (tier0-graph-employee-profile REQ-2). A mutable HR profile
    /// attribute, NOT an identity key; <see cref="ApplyEmployeeProfile"/> is its only writer
    /// (static gate in Tier0WorkforceArchitectureTests).</summary>
    public string? EmployeeId { get; private set; }

    /// <summary>Thai given name refreshed from the HR source on every Tier 0 login (REQ-3.6/3.13).</summary>
    public string? FirstName { get; private set; }

    /// <summary>Thai family name refreshed from the HR source on every Tier 0 login (REQ-3.7/3.13).</summary>
    public string? LastName { get; private set; }

    private User() { }

    private User(
        Guid id, string provider, Guid? tenantId, string? subject, string? email, Tier tier, DateTime createdAt)
        : base(id)
    {
        Provider = provider;
        TenantId = tenantId;
        Subject = subject;
        Email = AdminContactEmail.TryNormalize(email, out var normalizedEmail) ? normalizedEmail : null;
        Tier = tier;
        Status = UserStatus.Active;
        CreatedAt = createdAt;
        Version = 1;
    }

    /// <summary>The first Super Admin bootstrapping from the config allowlist on first login (REQ-5.1). The
    /// subject is bound immediately (the caller has authenticated).</summary>
    public static User SelfProvision(string provider, string subject, string email, DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (string.Equals(provider.Trim(), MicrosoftProvider, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Microsoft identities require a tenant-aware factory.", nameof(provider));
        if (!AdminContactEmail.TryNormalize(email, out var normalizedEmail) || normalizedEmail is null)
            throw new ArgumentException("A valid contact email is required.", nameof(email));
        return new User(Guid.NewGuid(), provider.Trim(), tenantId: null, subject.Trim(), normalizedEmail,
            Tier.Super, createdAt);
    }

    /// <summary>A Scoped admin invited by a Super (REQ-3.4): keyed by verified email, with an unbound
    /// <see cref="Subject"/> until the invitee's first login.</summary>
    public static User CreateScoped(string email, DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (!AdminContactEmail.TryNormalize(email, out var normalizedEmail) || normalizedEmail is null)
            throw new ArgumentException("A valid contact email is required.", nameof(email));
        return new User(Guid.NewGuid(), provider: GoogleProvider, tenantId: null, subject: null, normalizedEmail,
            Tier.Scoped, createdAt);
    }

    /// <summary>Creates a pre-bound least-privilege Microsoft account from an approved immutable tuple.</summary>
    public static User CreateScopedMicrosoft(Guid tenantId, Guid objectId, string? email, DateTime createdAt) =>
        NewMicrosoft(tenantId, objectId, email, createdAt);

    /// <summary>Creates a roleless, merchant-access-free JIT account from the validated immutable tuple.</summary>
    public static User JitProvisionMicrosoft(Guid tenantId, Guid objectId, string? email, DateTime createdAt) =>
        NewMicrosoft(tenantId, objectId, email, createdAt);

    private static User NewMicrosoft(Guid tenantId, Guid objectId, string? email, DateTime createdAt)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Workforce tenant ID cannot be empty.", nameof(tenantId));
        if (objectId == Guid.Empty)
            throw new ArgumentException("Microsoft object ID cannot be empty.", nameof(objectId));

        return new User(
            Guid.NewGuid(), MicrosoftProvider, tenantId, objectId.ToString("D"), email, Tier.Scoped, createdAt);
    }

    /// <summary>Binds the provider identity to an invited account on its first login (REQ-3.5). Idempotent
    /// re-binding is rejected — a bound account is resolved by (provider, subject), never re-bound.</summary>
    public void BindSubject(string provider, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (string.Equals(provider.Trim(), MicrosoftProvider, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Microsoft identities cannot use the historical bind path.", nameof(provider));
        if (Subject is not null || TenantId is not null)
            throw new InvalidOperationException("This admin account already has an immutable identity.");
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

    /// <summary>Replaces the resolved three-field employee profile on login.
    /// Org-profile fields and <see cref="AuthorizationVersion"/> are never touched.</summary>
    public EmployeeProfileChange ApplyEmployeeProfile(string employeeId, string firstName, string lastName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        var employeeBound = EmployeeId is null;
        var employeeIdChanged = !string.Equals(EmployeeId, employeeId, StringComparison.Ordinal);
        var namesChanged = !string.Equals(FirstName, firstName, StringComparison.Ordinal)
            || !string.Equals(LastName, lastName, StringComparison.Ordinal);
        if (!employeeIdChanged && !namesChanged)
            return new EmployeeProfileChange(false, false, false, false);

        EmployeeId = employeeId;
        FirstName = firstName;
        LastName = lastName;
        BumpResourceVersion();
        return new EmployeeProfileChange(true, employeeBound, employeeIdChanged, namesChanged);
    }
}
