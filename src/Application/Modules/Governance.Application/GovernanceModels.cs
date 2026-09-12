using BuildingBlocks.Application;
using Governance.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Governance.Application;

public sealed record GovernanceAccess(
    Guid ActorId,
    bool IsUnrestricted,
    IReadOnlySet<Guid> Merchants,
    IReadOnlySet<string> Permissions)
{
    public bool Allows(Guid? merchantId) => merchantId is { } id
        ? IsUnrestricted || Merchants.Contains(id)
        : IsUnrestricted;
}

public sealed record ApprovalQuery(
    int Page,
    int Limit,
    string? Search,
    string? Action,
    ApprovalStatus? Status,
    Guid? MerchantId,
    DateTime? From,
    DateTime? To,
    GovernanceAccess Access);

public sealed record ApprovalListItem(
    Guid ApprovalId,
    string Scope,
    Guid? MerchantId,
    string Action,
    Guid MakerId,
    string TargetType,
    string TargetId,
    string Status,
    DateTime CreatedAt,
    long Version);

public sealed record ApprovalDetail(
    Guid ApprovalId,
    string Scope,
    Guid? MerchantId,
    string Action,
    string RequiredPermission,
    Guid MakerId,
    string TargetType,
    string TargetId,
    string TargetVersion,
    string Status,
    Guid? CheckerId,
    string? DecisionReason,
    DateTime? DecidedAt,
    string? ExecutionOutcome,
    DateTime? ExecutedAt,
    string CorrelationId,
    DateTime CreatedAt,
    long Version);

public sealed record DecisionIntent(
    Guid ApprovalId,
    ApprovalDecision Decision,
    string Reason,
    long ExpectedVersion,
    string ExpectedTargetVersion,
    string IdempotencyKey,
    string CorrelationId,
    GovernanceAccess Access);

public sealed record DecisionResult(ApprovalDetail Approval, bool Replayed);

public static class DecisionIntentHasher
{
    public static string Compute(DecisionIntent intent)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            approvalId = intent.ApprovalId,
            decision = (int)intent.Decision,
            reason = intent.Reason.Trim(),
            expectedVersion = intent.ExpectedVersion,
            expectedTargetVersion = intent.ExpectedTargetVersion.Trim(),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record AuditQuery(
    int Page,
    int Limit,
    Guid? ActorId,
    string? Action,
    string? Resource,
    string? Result,
    Guid? MerchantId,
    DateTime? From,
    DateTime? To,
    GovernanceAccess Access);

public sealed record AuditListItem(
    Guid AuditId,
    string Scope,
    Guid? MerchantId,
    long Sequence,
    Guid ActorId,
    string Action,
    string ResourceType,
    string ResourceId,
    string Result,
    Guid? ApprovalId,
    string CorrelationId,
    DateTime OccurredAt);

public sealed record AuditDetail(
    Guid AuditId,
    string Scope,
    Guid? MerchantId,
    long Sequence,
    Guid ActorId,
    string Action,
    string ResourceType,
    string ResourceId,
    string Result,
    string Changes,
    Guid? ApprovalId,
    string? ResourceVersion,
    string CorrelationId,
    DateTime OccurredAt,
    string PreviousHash,
    string Hash);

public sealed class GovernanceAccessDeniedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AuditIntegrityException(string message) : Exception(message);

public sealed record AuditAnchorCheckpoint(
    string ScopeKey,
    long Sequence,
    string Hash,
    DateTime AnchoredAt);

public interface IAuditAnchorStore
{
    bool IsEnabled { get; }
    Task<IReadOnlyDictionary<string, AuditAnchorCheckpoint>> ReadAllLatestAsync(
        CancellationToken cancellationToken);
    Task AppendAsync(AuditAnchorCheckpoint checkpoint, CancellationToken cancellationToken);
}

public interface IGovernanceStore
{
    Task<PagedResult<ApprovalListItem>> ListApprovalsAsync(ApprovalQuery query, CancellationToken cancellationToken);
    Task<ApprovalDetail?> GetApprovalAsync(Guid approvalId, GovernanceAccess access, CancellationToken cancellationToken);
    Task<DecisionResult> DecideAsync(DecisionIntent intent, CancellationToken cancellationToken);
    Task ReceiveAsync(Contracts.ApprovalRequested message, CancellationToken cancellationToken);
    Task ReceiveAsync(Contracts.ApprovalExecutionReported message, CancellationToken cancellationToken);
    Task<PagedResult<AuditListItem>> ListAuditsAsync(AuditQuery query, CancellationToken cancellationToken);
    Task<AuditDetail?> GetAuditAsync(Guid auditId, GovernanceAccess access, CancellationToken cancellationToken);
}
