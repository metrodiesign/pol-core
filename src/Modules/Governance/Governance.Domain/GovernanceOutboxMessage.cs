namespace Governance.Domain;

public sealed class GovernanceOutboxMessage
{
    public Guid Id { get; private set; }
    public GovernanceScopeKind ScopeKind { get; private set; }
    public Guid? MerchantId { get; private set; }
    public string Type { get; private set; } = default!;
    public string SchemaVersion { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public string? LeaseOwner { get; private set; }

    private GovernanceOutboxMessage() { }

    public static GovernanceOutboxMessage Create(
        Guid id, GovernanceScopeKind scopeKind, Guid? merchantId,
        string type, string schemaVersion, string payload, DateTime occurredAt)
    {
        ApprovalRequest.ValidateScope(scopeKind, merchantId);
        return new()
        {
            Id = id == Guid.Empty ? throw new ArgumentException("Event identifier is required.", nameof(id)) : id,
            ScopeKind = scopeKind,
            MerchantId = merchantId,
            Type = ApprovalRequest.Required(type, nameof(type), 200),
            SchemaVersion = ApprovalRequest.Required(schemaVersion, nameof(schemaVersion), 16),
            Payload = ApprovalRequest.Required(payload, nameof(payload), 32_768),
            OccurredAt = occurredAt,
        };
    }

    public void MarkProcessed(DateTime now)
    {
        ProcessedAt = now;
        Error = null;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    public void MarkFailed(string error)
    {
        Error = error.Length <= 1000 ? error : error[..1000];
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }
}
