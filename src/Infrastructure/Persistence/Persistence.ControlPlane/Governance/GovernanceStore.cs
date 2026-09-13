using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using Contracts;
using Governance.Application;
using Governance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Governance;

internal sealed class GovernanceStore(
    ControlPlaneDbContext db,
    IUnitOfWork unitOfWork,
    IClock clock,
    IAuditAnchorStore auditAnchors,
    GovernanceSqlLockManager locks,
    GovernanceAuditAppender audits)
    : IGovernanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<ApprovalListItem>> ListApprovalsAsync(
        ApprovalQuery query, CancellationToken cancellationToken)
    {
        var source = ApplyAccess(db.ApprovalRequests.AsNoTracking(), query.Access);
        if (query.MerchantId is { } merchantId)
            source = query.Access.Allows(merchantId) ? source.Where(x => x.MerchantId == merchantId) : source.Where(_ => false);
        if (query.Status is { } status)
            source = source.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Action))
            source = source.Where(x => x.Action == query.Action.Trim());
        if (query.From is { } from)
            source = source.Where(x => x.CreatedAt >= from);
        if (query.To is { } to)
            source = source.Where(x => x.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            if (Guid.TryParse(search, out var approvalId))
                source = source.Where(x => x.Id == approvalId || x.TargetId.Contains(search));
            else
                source = source.Where(x => x.TargetId.Contains(search) || x.TargetType.Contains(search));
        }

        var total = await source.LongCountAsync(cancellationToken);
        var items = await source.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit)
            .Select(x => new ApprovalListItem(
                x.Id,
                x.ScopeKind == GovernanceScopeKind.Platform ? "platform" : "merchant",
                x.MerchantId,
                x.Action,
                x.MakerId,
                x.TargetType,
                x.TargetId,
                x.Status.ToString().ToLower(),
                x.CreatedAt,
                x.Version))
            .ToListAsync(cancellationToken);
        return new PagedResult<ApprovalListItem>(items, query.Page, query.Limit, total);
    }

    public async Task<ApprovalDetail?> GetApprovalAsync(
        Guid approvalId, GovernanceAccess access, CancellationToken cancellationToken)
    {
        var approval = await ApplyAccess(db.ApprovalRequests.AsNoTracking(), access)
            .SingleOrDefaultAsync(x => x.Id == approvalId, cancellationToken);
        return approval is null ? null : ToDetail(approval);
    }

    public async Task<DecisionResult> DecideAsync(DecisionIntent intent, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var operation = intent.Decision == ApprovalDecision.Approve ? "ApproveRequest" : "RejectRequest";
            await locks.AcquireAsync(OperationLock(intent.Access.ActorId, operation, intent.IdempotencyKey), ct);
            var requestHash = DecisionIntentHasher.Compute(intent);
            var prior = await db.OperationRecords.SingleOrDefaultAsync(x =>
                x.ActorId == intent.Access.ActorId && x.Operation == operation
                && x.IdempotencyKey == intent.IdempotencyKey, ct);
            if (prior is not null)
            {
                if (!prior.Matches(requestHash))
                    throw new ConflictException("The idempotency key was reused for a different decision.", "idempotency_key_reused");
                if (prior.Status == OperationStatus.InProgress || prior.ResponseBody is null)
                    throw new ConflictException("The decision is still in progress.", "operation_in_progress");
                var replay = JsonSerializer.Deserialize<ApprovalDetail>(prior.ResponseBody, JsonOptions)
                    ?? throw new InvalidOperationException("Recorded approval response is invalid.");
                return new DecisionResult(replay, Replayed: true);
            }

            var approval = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == intent.ApprovalId, ct)
                ?? throw new NotFoundException("The approval request was not found.");
            if (!intent.Access.Allows(approval.MerchantId))
                throw new GovernanceAccessDeniedException("merchant_scope_forbidden", "The approval is outside the admin scope.");
            if (!intent.Access.Permissions.Contains(approval.RequiredPermission))
                throw new GovernanceAccessDeniedException(
                    "underlying_permission_forbidden", "The checker lacks the permission required by the staged action.");

            try
            {
                approval.Decide(
                    intent.Decision, intent.Access.ActorId, intent.Reason, intent.ExpectedVersion,
                    intent.ExpectedTargetVersion, clock.UtcNow);
            }
            catch (ApprovalRuleException ex) when (ex.Code == "maker_cannot_decide")
            {
                throw new GovernanceAccessDeniedException(ex.Code, ex.Message);
            }
            catch (ApprovalRuleException ex)
            {
                throw new ConflictException(ex.Message, ex.Code);
            }

            var eventId = Guid.CreateVersion7();
            var decision = intent.Decision == ApprovalDecision.Approve ? "approved" : "rejected";
            db.ApprovalEvents.Add(ApprovalEvent.Create(
                eventId, approval.Id, approval.ScopeKind, approval.MerchantId, "decided",
                intent.Access.ActorId, decision, intent.CorrelationId, clock.UtcNow));
            var notification = new ApprovalDecided(
                eventId, approval.Id,
                approval.ScopeKind == GovernanceScopeKind.Platform ? "platform" : "merchant",
                approval.MerchantId, decision, intent.Access.ActorId, approval.DecisionReason!,
                approval.TargetType, approval.TargetId, approval.TargetVersion, intent.CorrelationId, clock.UtcNow);
            db.GovernanceOutboxMessages.Add(GovernanceOutboxMessage.Create(
                eventId, approval.ScopeKind, approval.MerchantId, ApprovalDecided.EventType,
                ApprovalDecided.SchemaVersion, JsonSerializer.Serialize(notification, JsonOptions), clock.UtcNow));

            await audits.AppendAsync(
                approval.ScopeKind, approval.MerchantId, intent.Access.ActorId, "approval.decided",
                "approval", approval.Id.ToString("D"), decision,
                JsonSerializer.Serialize(new { decision, reason = approval.DecisionReason }, JsonOptions),
                approval.Id, approval.TargetVersion, intent.CorrelationId, clock.UtcNow, ct);

            var operationRecord = OperationRecord.Create(
                intent.Access.ActorId, operation, intent.IdempotencyKey, requestHash,
                approval.ScopeKind, approval.MerchantId, clock.UtcNow, clock.UtcNow.AddHours(24));
            var detail = ToDetail(approval);
            operationRecord.Complete(202, JsonSerializer.Serialize(detail, JsonOptions), succeeded: true, clock.UtcNow);
            db.OperationRecords.Add(operationRecord);
            await unitOfWork.SaveChangesAsync(ct);
            return new DecisionResult(detail, Replayed: false);
        }, cancellationToken);
        return result;
    }

    public async Task ReceiveAsync(ApprovalRequested message, CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireAsync(EventLock(message.EventId), ct);
            if (await db.ApprovalEvents.AnyAsync(x => x.SourceEventId == message.EventId, ct))
                return true;

            var scopeKind = ParseScope(message.Scope);
            var approval = ApprovalRequest.Create(
                message.ApprovalId, scopeKind, message.MerchantId, message.Action, message.RequiredPermission,
                message.MakerId, message.TargetType, message.TargetId, message.TargetVersion,
                message.CorrelationId, message.OccurredAt);
            db.ApprovalRequests.Add(approval);
            db.ApprovalEvents.Add(ApprovalEvent.Create(
                message.EventId, message.ApprovalId, scopeKind, message.MerchantId, "requested",
                message.MakerId, null, message.CorrelationId, message.OccurredAt));
            await audits.AppendAsync(
                scopeKind, message.MerchantId, message.MakerId, "approval.created", "approval",
                message.ApprovalId.ToString("D"), "pending",
                JsonSerializer.Serialize(new { message.Action, message.TargetType, message.TargetId }, JsonOptions),
                message.ApprovalId, message.TargetVersion, message.CorrelationId, clock.UtcNow, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task ReceiveAsync(ApprovalExecutionReported message, CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireAsync(EventLock(message.EventId), ct);
            if (await db.ApprovalEvents.AnyAsync(x => x.SourceEventId == message.EventId, ct))
                return true;
            var approval = await db.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == message.ApprovalId, ct)
                ?? throw new NotFoundException("The approval request was not found.");
            approval.RecordExecution(message.Outcome, message.Succeeded, message.Unknown, message.OccurredAt);
            db.ApprovalEvents.Add(ApprovalEvent.Create(
                message.EventId, message.ApprovalId, approval.ScopeKind, approval.MerchantId, "executed",
                message.ExecutorId, message.Outcome, message.CorrelationId, message.OccurredAt));
            await audits.AppendAsync(
                approval.ScopeKind, approval.MerchantId, message.ExecutorId, "approval.executed", "approval",
                approval.Id.ToString("D"), approval.Status.ToString().ToLower(),
                JsonSerializer.Serialize(new { message.Succeeded, message.Unknown, message.Outcome }, JsonOptions),
                approval.Id, message.ResourceVersion, message.CorrelationId, clock.UtcNow, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task<PagedResult<AuditListItem>> ListAuditsAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        await VerifyAccessibleAsync(query.Access, cancellationToken);
        var source = ApplyAccess(db.AuditRecords.AsNoTracking(), query.Access);
        if (query.MerchantId is { } merchantId)
            source = query.Access.Allows(merchantId) ? source.Where(x => x.MerchantId == merchantId) : source.Where(_ => false);
        if (query.ActorId is { } actorId)
            source = source.Where(x => x.ActorId == actorId);
        if (!string.IsNullOrWhiteSpace(query.Action))
            source = source.Where(x => x.Action == query.Action.Trim());
        if (!string.IsNullOrWhiteSpace(query.Result))
            source = source.Where(x => x.Result == query.Result.Trim());
        if (!string.IsNullOrWhiteSpace(query.Resource))
        {
            var resource = query.Resource.Trim();
            source = source.Where(x => x.ResourceId.Contains(resource) || x.ResourceType.Contains(resource));
        }
        if (query.From is { } from)
            source = source.Where(x => x.OccurredAt >= from);
        if (query.To is { } to)
            source = source.Where(x => x.OccurredAt <= to);

        var total = await source.LongCountAsync(cancellationToken);
        var items = await source.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit)
            .Select(x => new AuditListItem(
                x.Id,
                x.ScopeKind == GovernanceScopeKind.Platform ? "platform" : "merchant",
                x.MerchantId,
                x.Sequence,
                x.ActorId,
                x.Action,
                x.ResourceType,
                x.ResourceId,
                x.Result,
                x.ApprovalId,
                x.CorrelationId,
                x.OccurredAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<AuditListItem>(items, query.Page, query.Limit, total);
    }

    public async Task<AuditDetail?> GetAuditAsync(
        Guid auditId, GovernanceAccess access, CancellationToken cancellationToken)
    {
        var record = await ApplyAccess(db.AuditRecords.AsNoTracking(), access)
            .SingleOrDefaultAsync(x => x.Id == auditId, cancellationToken);
        if (record is null)
            return null;
        await VerifyScopeAsync(record.ScopeKey, cancellationToken);
        return new AuditDetail(
            record.Id,
            record.ScopeKind == GovernanceScopeKind.Platform ? "platform" : "merchant",
            record.MerchantId,
            record.Sequence,
            record.ActorId,
            record.Action,
            record.ResourceType,
            record.ResourceId,
            record.Result,
            record.Changes,
            record.ApprovalId,
            record.ResourceVersion,
            record.CorrelationId,
            record.OccurredAt,
            Convert.ToHexString(record.PreviousHash).ToLowerInvariant(),
            Convert.ToHexString(record.Hash).ToLowerInvariant());
    }

    private async Task VerifyAccessibleAsync(GovernanceAccess access, CancellationToken cancellationToken)
    {
        var heads = db.AuditHeads.AsNoTracking();
        heads = access.IsUnrestricted
            ? heads
            : heads.Where(x => x.MerchantId.HasValue && access.Merchants.Contains(x.MerchantId.Value));
        var scopeKeys = await heads.Select(x => x.Id).ToListAsync(cancellationToken);
        var anchors = auditAnchors.IsEnabled
            ? await auditAnchors.ReadAllLatestAsync(cancellationToken)
            : null;
        foreach (var scopeKey in scopeKeys)
            await VerifyScopeAsync(scopeKey, cancellationToken, anchors);
    }

    private async Task VerifyScopeAsync(
        string scopeKey,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, AuditAnchorCheckpoint>? knownAnchors = null)
    {
        var head = await db.AuditHeads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scopeKey, cancellationToken)
            ?? throw new AuditIntegrityException("Audit chain head is missing.");
        var records = await db.AuditRecords.AsNoTracking().Where(x => x.ScopeKey == scopeKey)
            .OrderBy(x => x.Sequence).ToListAsync(cancellationToken);
        var prior = AuditRecord.Genesis;
        long expected = 1;
        foreach (var record in records)
        {
            if (record.Sequence != expected
                || !CryptographicOperations.FixedTimeEquals(record.PreviousHash, prior)
                || !record.HasValidHash())
                throw new AuditIntegrityException("Audit chain verification failed.");
            prior = record.Hash;
            expected++;
        }
        if (head.LastSequence != records.Count || !CryptographicOperations.FixedTimeEquals(head.LastHash, prior))
            throw new AuditIntegrityException("Audit chain head does not match its records.");

        if (!auditAnchors.IsEnabled || head.LastSequence == 0)
            return;
        var anchors = knownAnchors ?? await auditAnchors.ReadAllLatestAsync(cancellationToken);
        if (!anchors.TryGetValue(scopeKey, out var anchor)
            || anchor.Sequence < 1
            || anchor.Sequence > records.Count
            || !TryDecodeAnchorHash(anchor.Hash, out var anchoredHash)
            || !CryptographicOperations.FixedTimeEquals(
                anchoredHash, records[checked((int)anchor.Sequence - 1)].Hash))
            throw new AuditIntegrityException("Audit chain does not match its signed external anchor.");
    }

    private static bool TryDecodeAnchorHash(string hash, out byte[] bytes)
    {
        bytes = [];
        if (hash.Length != 64)
            return false;
        try
        {
            bytes = Convert.FromHexString(hash);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IQueryable<ApprovalRequest> ApplyAccess(
        IQueryable<ApprovalRequest> source, GovernanceAccess access) => access.IsUnrestricted
            ? source
            : source.Where(x => x.MerchantId.HasValue && access.Merchants.Contains(x.MerchantId.Value));

    private static IQueryable<AuditRecord> ApplyAccess(
        IQueryable<AuditRecord> source, GovernanceAccess access) => access.IsUnrestricted
            ? source
            : source.Where(x => x.MerchantId.HasValue && access.Merchants.Contains(x.MerchantId.Value));

    private static GovernanceScopeKind ParseScope(string scope) => scope switch
    {
        "platform" => GovernanceScopeKind.Platform,
        "merchant" => GovernanceScopeKind.Merchant,
        _ => throw new ArgumentException("Approval scope must be platform or merchant.", nameof(scope)),
    };

    private static string EventLock(Guid eventId) => $"governance-event:{eventId:D}";

    private static string OperationLock(Guid actorId, string operation, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{actorId:D}\n{operation}\n{key}"));
        return $"governance-operation:{Convert.ToHexString(bytes)}";
    }

    private static ApprovalDetail ToDetail(ApprovalRequest approval) => new(
        approval.Id,
        approval.ScopeKind == GovernanceScopeKind.Platform ? "platform" : "merchant",
        approval.MerchantId,
        approval.Action,
        approval.RequiredPermission,
        approval.MakerId,
        approval.TargetType,
        approval.TargetId,
        approval.TargetVersion,
        approval.Status.ToString().ToLower(),
        approval.CheckerId,
        approval.DecisionReason,
        approval.DecidedAt,
        approval.ExecutionOutcome,
        approval.ExecutedAt,
        approval.CorrelationId,
        approval.CreatedAt,
        approval.Version);
}
