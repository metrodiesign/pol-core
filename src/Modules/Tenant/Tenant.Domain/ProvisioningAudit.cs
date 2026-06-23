using SharedKernel;

namespace Tenant.Domain;

/// <summary>
/// Append-only record that a tenant was provisioned via the cross-tenant <c>pol_admin</c> bypass.
/// Written in the same transaction as the provisioning itself (so it commits/rolls back with it) and
/// never holds a secret. Control-plane only — not under the tenant RLS predicate.
/// </summary>
public sealed class ProvisioningAudit : Entity<Guid>
{
    public Guid TenantId { get; private set; }

    public string TenantCode { get; private set; } = default!;

    /// <summary>The acting admin's stable identity (Google subject claim).</summary>
    public string AdminSubject { get; private set; } = default!;

    public string CorrelationId { get; private set; } = default!;

    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private ProvisioningAudit() { }

    private ProvisioningAudit(Guid id, Guid tenantId, string tenantCode, string adminSubject,
        string correlationId, DateTime occurredAtUtc) : base(id)
    {
        TenantId = tenantId;
        TenantCode = tenantCode;
        AdminSubject = adminSubject;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }

    public static ProvisioningAudit Create(Guid tenantId, string tenantCode, string adminSubject,
        string correlationId, DateTime occurredAtUtc)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new ProvisioningAudit(Guid.NewGuid(), tenantId, tenantCode, adminSubject, correlationId, occurredAtUtc);
    }
}
