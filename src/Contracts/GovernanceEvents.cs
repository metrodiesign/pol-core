using Mediator;

namespace Contracts;

/// <summary>PII/secret-free owner event that creates one Governance request.</summary>
public sealed record ApprovalRequested(
    Guid EventId,
    Guid ApprovalId,
    string Scope,
    Guid? MerchantId,
    string Action,
    string RequiredPermission,
    Guid MakerId,
    string TargetType,
    string TargetId,
    string TargetVersion,
    string CorrelationId,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "governance.approval-requested.v1";
    public const string SchemaVersion = "v1";
}

/// <summary>Governance decision delivered to target owner; target state remains owner-local.</summary>
public sealed record ApprovalDecided(
    Guid EventId,
    Guid ApprovalId,
    string Scope,
    Guid? MerchantId,
    string Decision,
    Guid CheckerId,
    string Reason,
    string TargetType,
    string TargetId,
    string TargetVersion,
    string CorrelationId,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "governance.approval-decided.v1";
    public const string SchemaVersion = "v1";
}

/// <summary>Owner terminal/unknown outcome delivered back to Governance.</summary>
public sealed record ApprovalExecutionReported(
    Guid EventId,
    Guid ApprovalId,
    Guid ExecutorId,
    bool Succeeded,
    bool Unknown,
    string Outcome,
    string? ResourceVersion,
    string CorrelationId,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "governance.approval-execution-reported.v1";
    public const string SchemaVersion = "v1";
}
