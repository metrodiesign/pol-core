using SharedKernel;

namespace Merchants.Domain;

/// <summary>
/// Append-only record that a merchant was provisioned via the cross-merchant <c>pol_admin</c> bypass.
/// Written in the same transaction as the provisioning itself (so it commits/rolls back with it) and
/// never holds a secret. Control-plane only — not under the merchant RLS predicate.
/// </summary>
public sealed class ProvisioningAudit : Entity<Guid>
{
    public Guid MerchantId { get; private set; }

    public string MerchantCode { get; private set; } = default!;

    /// <summary>The acting admin's stable identity (Google subject claim).</summary>
    public string AdminSubject { get; private set; } = default!;

    public string CorrelationId { get; private set; } = default!;

    public DateTime OccurredAt { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private ProvisioningAudit() { }

    private ProvisioningAudit(Guid id, Guid merchantId, string merchantCode, string adminSubject,
        string correlationId, DateTime occurredAt) : base(id)
    {
        MerchantId = merchantId;
        MerchantCode = merchantCode;
        AdminSubject = adminSubject;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    public static ProvisioningAudit Create(Guid merchantId, string merchantCode, string adminSubject,
        string correlationId, DateTime occurredAt)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new ProvisioningAudit(Guid.NewGuid(), merchantId, merchantCode, adminSubject, correlationId, occurredAt);
    }
}
