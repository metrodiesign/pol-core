using SharedKernel;

namespace Identity.Domain;

/// <summary>
/// A person who can act for a tenant in the Tenant Console. The platform — never the token — decides the
/// tenant and role: a new user is created <see cref="TenantUserStatus.PendingApproval"/> with no TenantId,
/// and an admin binds both at approval (reference 2.5). <see cref="Subject"/> is the stable Google subject.
/// </summary>
public sealed class TenantUser : AggregateRoot<Guid>
{
    /// <summary>Stable external subject (Google <c>sub</c>). Unique.</summary>
    public string Subject { get; private set; } = default!;

    public string Email { get; private set; } = default!;

    /// <summary>Null until approved; then the tenant the admin selected.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>Null until approved; then the role the admin assigned.</summary>
    public TenantUserRole? Role { get; private set; }

    public TenantUserStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private TenantUser() { }

    private TenantUser(Guid id, string subject, string email, DateTime createdAtUtc) : base(id)
    {
        Subject = subject;
        Email = email;
        Status = TenantUserStatus.PendingApproval;
        TenantId = null;
        Role = null;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Creates a pending applicant. Tenant + role are decided at approval, NOT here.</summary>
    public static TenantUser Register(string subject, string email, DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new TenantUser(Guid.NewGuid(), subject.Trim(), email.Trim(), createdAtUtc);
    }

    /// <summary>Binds the tenant the admin selected + the assigned role and activates. The TenantId comes
    /// from the admin's validated choice (REQ-5.3), never from the applicant.</summary>
    public void Approve(Guid tenantId, TenantUserRole role, DateTime approvedAtUtc)
    {
        if (Status != TenantUserStatus.PendingApproval)
            throw new InvalidOperationException("Only a pending tenant user can be approved.");
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A valid tenant id is required.", nameof(tenantId));

        TenantId = tenantId;
        Role = role;
        Status = TenantUserStatus.Active;
        _ = approvedAtUtc; // reserved for an ApprovedAtUtc column if needed; kept explicit for the audit row
    }

    public void Suspend()
    {
        if (Status == TenantUserStatus.Suspended)
            return;
        Status = TenantUserStatus.Suspended;
    }
}
