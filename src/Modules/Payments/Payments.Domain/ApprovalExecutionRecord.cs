namespace Payments.Domain;

public enum ApprovalExecutionState
{
    Processing = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>Durable owner-side claim for one Governance approval decision.</summary>
public sealed class ApprovalExecutionRecord
{
    public Guid EventId { get; private set; }
    public Guid ApprovalId { get; private set; }
    public Guid MerchantId { get; private set; }
    public string TargetType { get; private set; } = default!;
    public string TargetId { get; private set; } = default!;
    public string Decision { get; private set; } = default!;
    public ApprovalExecutionState State { get; private set; }
    public string? Outcome { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private ApprovalExecutionRecord() { }

    public static ApprovalExecutionRecord Claim(
        Guid eventId,
        Guid approvalId,
        Guid merchantId,
        string targetType,
        string targetId,
        string decision,
        DateTime now)
    {
        if (eventId == Guid.Empty || approvalId == Guid.Empty || merchantId == Guid.Empty)
            throw new ArgumentException("Event, approval and merchant identifiers are required.");
        return new ApprovalExecutionRecord
        {
            EventId = eventId,
            ApprovalId = approvalId,
            MerchantId = merchantId,
            TargetType = Required(targetType, nameof(targetType), 64),
            TargetId = Required(targetId, nameof(targetId), 200),
            Decision = Required(decision, nameof(decision), 16),
            State = ApprovalExecutionState.Processing,
            CreatedAt = now,
        };
    }

    public void Complete(bool succeeded, string outcome, DateTime now)
    {
        if (State != ApprovalExecutionState.Processing)
            throw new InvalidOperationException("Approval execution is already terminal.");
        Outcome = Required(outcome, nameof(outcome), 120);
        State = succeeded ? ApprovalExecutionState.Succeeded : ApprovalExecutionState.Failed;
        CompletedAt = now;
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return trimmed;
    }
}
