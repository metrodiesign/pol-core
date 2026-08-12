using BuildingBlocks.Application;
using Contracts;
using Mediator;

namespace Governance.Application;

public sealed record ListApprovalsQuery(ApprovalQuery Query) : IQuery<PagedResult<ApprovalListItem>>;
public sealed record GetApprovalQuery(Guid ApprovalId, GovernanceAccess Access) : IQuery<ApprovalDetail?>;
public sealed record DecideApprovalCommand(DecisionIntent Intent) : ICommand<DecisionResult>;
public sealed record ListAuditsQuery(AuditQuery Query) : IQuery<PagedResult<AuditListItem>>;
public sealed record GetAuditQuery(Guid AuditId, GovernanceAccess Access) : IQuery<AuditDetail?>;

public sealed class ListApprovalsHandler(IGovernanceStore store)
    : IQueryHandler<ListApprovalsQuery, PagedResult<ApprovalListItem>>
{
    public async ValueTask<PagedResult<ApprovalListItem>> Handle(ListApprovalsQuery query, CancellationToken ct) =>
        await store.ListApprovalsAsync(query.Query, ct);
}

public sealed class GetApprovalHandler(IGovernanceStore store) : IQueryHandler<GetApprovalQuery, ApprovalDetail?>
{
    public async ValueTask<ApprovalDetail?> Handle(GetApprovalQuery query, CancellationToken ct) =>
        await store.GetApprovalAsync(query.ApprovalId, query.Access, ct);
}

public sealed class DecideApprovalHandler(IGovernanceStore store) : ICommandHandler<DecideApprovalCommand, DecisionResult>
{
    public async ValueTask<DecisionResult> Handle(DecideApprovalCommand command, CancellationToken ct) =>
        await store.DecideAsync(command.Intent, ct);
}

public sealed class ListAuditsHandler(IGovernanceStore store) : IQueryHandler<ListAuditsQuery, PagedResult<AuditListItem>>
{
    public async ValueTask<PagedResult<AuditListItem>> Handle(ListAuditsQuery query, CancellationToken ct) =>
        await store.ListAuditsAsync(query.Query, ct);
}

public sealed class GetAuditHandler(IGovernanceStore store) : IQueryHandler<GetAuditQuery, AuditDetail?>
{
    public async ValueTask<AuditDetail?> Handle(GetAuditQuery query, CancellationToken ct) =>
        await store.GetAuditAsync(query.AuditId, query.Access, ct);
}

public sealed class ApprovalRequestedHandler(IGovernanceStore store) : INotificationHandler<ApprovalRequested>
{
    public async ValueTask Handle(ApprovalRequested notification, CancellationToken ct) =>
        await store.ReceiveAsync(notification, ct);
}

public sealed class ApprovalExecutionReportedHandler(IGovernanceStore store)
    : INotificationHandler<ApprovalExecutionReported>
{
    public async ValueTask Handle(ApprovalExecutionReported notification, CancellationToken ct) =>
        await store.ReceiveAsync(notification, ct);
}

/// <summary>Target-owner seam. Missing or ambiguous owner fails dispatch, so a decision is never acknowledged/lost.</summary>
public interface IApprovalDecisionExecutor
{
    bool CanHandle(string targetType);
    Task ExecuteAsync(ApprovalDecided decision, CancellationToken cancellationToken);
}

public sealed class ApprovalDecidedHandler(IEnumerable<IApprovalDecisionExecutor> executors)
    : INotificationHandler<ApprovalDecided>
{
    public async ValueTask Handle(ApprovalDecided notification, CancellationToken ct)
    {
        var matches = executors.Where(x => x.CanHandle(notification.TargetType)).Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"Expected one approval executor for target type '{notification.TargetType}', found {matches.Length}.");
        await matches[0].ExecuteAsync(notification, ct);
    }
}
