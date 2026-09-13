using SharedKernel;

namespace Governance.Domain;

public enum OperationStatus { InProgress = 1, Succeeded = 2, Failed = 3, Unknown = 4 }

/// <summary>Bounded replay ledger for ControlPlane mutations. Never stores a raw credential.</summary>
public sealed class OperationRecord : Entity<Guid>
{
    public Guid ActorId { get; private set; }
    public string Operation { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public GovernanceScopeKind ScopeKind { get; private set; }
    public Guid? MerchantId { get; private set; }
    public OperationStatus Status { get; private set; }
    public int? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private OperationRecord() { }

    public static OperationRecord Create(
        Guid actorId, string operation, string idempotencyKey, string requestHash,
        GovernanceScopeKind scopeKind, Guid? merchantId, DateTime createdAt, DateTime expiresAt)
    {
        if (actorId == Guid.Empty)
            throw new ArgumentException("Actor identifier is required.", nameof(actorId));
        if (expiresAt < createdAt.AddHours(24))
            throw new ArgumentException("Operation records must be retained for at least 24 hours.", nameof(expiresAt));
        ApprovalRequest.ValidateScope(scopeKind, merchantId);
        return new OperationRecord
        {
            Id = Guid.CreateVersion7(),
            ActorId = actorId,
            Operation = ApprovalRequest.Required(operation, nameof(operation), 120),
            IdempotencyKey = ApprovalRequest.Required(idempotencyKey, nameof(idempotencyKey), 200),
            RequestHash = ApprovalRequest.Required(requestHash, nameof(requestHash), 64),
            ScopeKind = scopeKind,
            MerchantId = merchantId,
            Status = OperationStatus.InProgress,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
    }

    public bool Matches(string requestHash) => string.Equals(RequestHash, requestHash, StringComparison.Ordinal);

    public void Complete(int responseStatus, string responseBody, bool succeeded, DateTime completedAt)
    {
        if (Status != OperationStatus.InProgress)
            return;
        if (responseBody.Length > 16_384)
            throw new ArgumentException("Operation response exceeds 16 KiB.", nameof(responseBody));
        ResponseStatus = responseStatus;
        ResponseBody = responseBody;
        Status = succeeded ? OperationStatus.Succeeded : OperationStatus.Failed;
        CompletedAt = completedAt;
    }
}
