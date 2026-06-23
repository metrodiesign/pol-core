using SharedKernel;

namespace Identity.Domain;

/// <summary>
/// Append-only record of a sensitive identity control-plane action performed via the pol_admin bypass —
/// currently the approval of a tenant user (reference 2.5 / 2.3 maker-checker spirit). Written in the same
/// transaction as the action; never holds a secret. Not under the tenant RLS predicate.
/// </summary>
public sealed class RegistrationAudit : Entity<Guid>
{
    public string Action { get; private set; } = default!;

    /// <summary>The acting admin's stable subject.</summary>
    public string AdminSubject { get; private set; } = default!;

    /// <summary>The tenant user the action targeted.</summary>
    public string TargetSubject { get; private set; } = default!;

    /// <summary>The role granted by the approval — audited so the trail records which role was assigned (REQ-5.6).</summary>
    public string Role { get; private set; } = default!;

    public Guid? TenantId { get; private set; }

    public string CorrelationId { get; private set; } = default!;

    public DateTime OccurredAtUtc { get; private set; }

    private RegistrationAudit() { }

    private RegistrationAudit(Guid id, string action, string adminSubject, string targetSubject,
        string role, Guid? tenantId, string correlationId, DateTime occurredAtUtc) : base(id)
    {
        Action = action;
        AdminSubject = adminSubject;
        TargetSubject = targetSubject;
        Role = role;
        TenantId = tenantId;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }

    public static RegistrationAudit ForApproval(string adminSubject, string targetSubject, Guid tenantId,
        string role, string correlationId, DateTime occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));

        return new RegistrationAudit(Guid.NewGuid(), "tenant-user.approve", adminSubject, targetSubject,
            role, tenantId, correlationId, occurredAtUtc);
    }
}
