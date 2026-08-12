using SharedKernel;

namespace Governance.Domain;

public enum GovernanceScopeKind { Platform = 1, Merchant = 2 }
public enum ApprovalStatus { Pending = 1, Approved = 2, Rejected = 3, Succeeded = 4, Failed = 5, Unknown = 6 }
public enum ApprovalDecision { Approve = 1, Reject = 2 }

public sealed class ApprovalRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Governance projection of an owner-staged action. It stores references only; target payload and secrets stay
/// in the owning module. <see cref="Version"/> is the optimistic token used by the decision endpoint.
/// </summary>
public sealed class ApprovalRequest : AggregateRoot<Guid>
{
    public GovernanceScopeKind ScopeKind { get; private set; }
    public Guid? MerchantId { get; private set; }
    public string Action { get; private set; } = default!;
    public string RequiredPermission { get; private set; } = default!;
    public Guid MakerId { get; private set; }
    public string TargetType { get; private set; } = default!;
    public string TargetId { get; private set; } = default!;
    public string TargetVersion { get; private set; } = default!;
    public ApprovalStatus Status { get; private set; }
    public Guid? CheckerId { get; private set; }
    public string? DecisionReason { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public string? ExecutionOutcome { get; private set; }
    public DateTime? ExecutedAt { get; private set; }
    public string CorrelationId { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }
    public long Version { get; private set; }

    private ApprovalRequest() { }

    public static ApprovalRequest Create(
        Guid id,
        GovernanceScopeKind scopeKind,
        Guid? merchantId,
        string action,
        string requiredPermission,
        Guid makerId,
        string targetType,
        string targetId,
        string targetVersion,
        string correlationId,
        DateTime createdAt)
    {
        if (id == Guid.Empty || makerId == Guid.Empty)
            throw new ArgumentException("Approval and maker identifiers are required.");
        ValidateScope(scopeKind, merchantId);
        return new ApprovalRequest
        {
            Id = id,
            ScopeKind = scopeKind,
            MerchantId = merchantId,
            Action = Required(action, nameof(action), 120),
            RequiredPermission = Required(requiredPermission, nameof(requiredPermission), 120),
            MakerId = makerId,
            TargetType = Required(targetType, nameof(targetType), 120),
            TargetId = Required(targetId, nameof(targetId), 200),
            TargetVersion = Required(targetVersion, nameof(targetVersion), 200),
            CorrelationId = Required(correlationId, nameof(correlationId), 128),
            CreatedAt = createdAt,
            Status = ApprovalStatus.Pending,
            Version = 1,
        };
    }

    public void Decide(
        ApprovalDecision decision,
        Guid checkerId,
        string reason,
        long expectedVersion,
        string expectedTargetVersion,
        DateTime decidedAt)
    {
        if (checkerId == Guid.Empty)
            throw new ArgumentException("Checker identifier is required.", nameof(checkerId));
        if (checkerId == MakerId)
            throw new ApprovalRuleException("maker_cannot_decide", "The maker cannot decide this approval.");
        if (Status != ApprovalStatus.Pending)
            throw new ApprovalRuleException("approval_not_pending", "The approval is no longer pending.");
        if (expectedVersion != Version)
            throw new ApprovalRuleException("approval_not_pending", "The approval version is stale.");
        if (!string.Equals(expectedTargetVersion, TargetVersion, StringComparison.Ordinal))
            throw new ApprovalRuleException("target_version_changed", "The target version changed after the request was staged.");

        CheckerId = checkerId;
        DecisionReason = Required(reason, nameof(reason), 1000);
        DecidedAt = decidedAt;
        Status = decision == ApprovalDecision.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        Version++;
    }

    public void RecordExecution(string outcome, bool succeeded, bool unknown, DateTime executedAt)
    {
        if (Status == ApprovalStatus.Rejected)
            throw new ApprovalRuleException("approval_rejected", "A rejected approval cannot execute.");
        if (Status is ApprovalStatus.Succeeded or ApprovalStatus.Failed or ApprovalStatus.Unknown)
            return;
        if (Status != ApprovalStatus.Approved)
            throw new ApprovalRuleException("approval_not_approved", "The approval has not been approved.");

        ExecutionOutcome = Required(outcome, nameof(outcome), 1000);
        ExecutedAt = executedAt;
        Status = unknown ? ApprovalStatus.Unknown : succeeded ? ApprovalStatus.Succeeded : ApprovalStatus.Failed;
        Version++;
    }

    internal static void ValidateScope(GovernanceScopeKind scopeKind, Guid? merchantId)
    {
        if (scopeKind == GovernanceScopeKind.Platform && merchantId is not null)
            throw new ArgumentException("Platform approvals cannot carry a merchant.", nameof(merchantId));
        if (scopeKind == GovernanceScopeKind.Merchant && (!merchantId.HasValue || merchantId.Value == Guid.Empty))
            throw new ArgumentException("Merchant approvals require a merchant.", nameof(merchantId));
    }

    internal static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return trimmed;
    }
}

/// <summary>Append-only request/decision/execution history and inbound-event idempotency claim.</summary>
public sealed class ApprovalEvent : Entity<Guid>
{
    public Guid SourceEventId { get; private set; }
    public Guid ApprovalId { get; private set; }
    public GovernanceScopeKind ScopeKind { get; private set; }
    public Guid? MerchantId { get; private set; }
    public string Kind { get; private set; } = default!;
    public Guid? ActorId { get; private set; }
    public string? Detail { get; private set; }
    public string CorrelationId { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }

    private ApprovalEvent() { }

    public static ApprovalEvent Create(
        Guid sourceEventId, Guid approvalId, GovernanceScopeKind scopeKind, Guid? merchantId,
        string kind, Guid? actorId, string? detail,
        string correlationId, DateTime occurredAt)
    {
        if (sourceEventId == Guid.Empty || approvalId == Guid.Empty)
            throw new ArgumentException("Event and approval identifiers are required.");
        ApprovalRequest.ValidateScope(scopeKind, merchantId);
        return new ApprovalEvent
        {
            Id = Guid.CreateVersion7(),
            SourceEventId = sourceEventId,
            ApprovalId = approvalId,
            ScopeKind = scopeKind,
            MerchantId = merchantId,
            Kind = ApprovalRequest.Required(kind, nameof(kind), 40),
            ActorId = actorId,
            Detail = detail is null ? null : ApprovalRequest.Required(detail, nameof(detail), 1000),
            CorrelationId = ApprovalRequest.Required(correlationId, nameof(correlationId), 128),
            OccurredAt = occurredAt,
        };
    }
}
