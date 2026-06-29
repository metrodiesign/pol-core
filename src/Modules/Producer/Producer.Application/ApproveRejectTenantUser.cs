using BuildingBlocks.Application;
using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>
/// Approves a producer onto a tenant the admin already validated (REQ-6). The Admin permission check
/// (<c>producer.approve</c>) AND the accessible-tenant floor run at the HOST before dispatch (critique B3); this
/// command receives an ALREADY-VALIDATED tenant id and lives in Producer.Application with NO Admin import. It runs
/// in ONE pol_admin transaction: validate the assigned roles exist + are Active (REQ-6.5), bind the tenant, set the
/// role assignments, flip the user Active, and audit. Idempotent: an already-Active target is a no-op success
/// (REQ-6.4). A non-PendingApproval target (Rejected/Suspended — must resubmit first) is a 409 (REQ-6.5).
/// </summary>
public sealed record ApproveTenantUserCommand(
    string Subject,
    Guid ValidatedTenantId,
    IReadOnlyList<string> RoleCodes,
    string ActingAdminSubject,
    Guid ActingAdminId,
    string CorrelationId) : ICommand<ApproveTenantUserResult>;

public sealed record ApproveTenantUserResult(Guid TenantUserId, ProducerAccountStatus Status, bool AlreadyActive);

public sealed class ApproveTenantUserHandler : ICommandHandler<ApproveTenantUserCommand, ApproveTenantUserResult>
{
    private readonly IProducerAccountRepository _accounts;
    private readonly IProducerTenantAssignmentRepository _assignments;
    private readonly IProducerRoleRepository _roles;
    private readonly IRegistrationAuditWriter _audit;
    private readonly IProducerUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ApproveTenantUserHandler(
        IProducerAccountRepository accounts, IProducerTenantAssignmentRepository assignments,
        IProducerRoleRepository roles, IRegistrationAuditWriter audit,
        IProducerUnitOfWork unitOfWork, IClock clock)
    {
        _accounts = accounts;
        _assignments = assignments;
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public ValueTask<ApproveTenantUserResult> Handle(ApproveTenantUserCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var now = _clock.UtcNow;
            var account = await _accounts.FindBySubjectAsync(command.Subject, ct)
                ?? throw new NotFoundException("The producer registration was not found."); // 404 (REQ-22.2)

            // Idempotent no-op for an already-Active target ONLY when bound to the same tenant (REQ-6.4); re-approving
            // onto a different tenant is rejected (a producer acts for exactly one tenant).
            if (account.Status == ProducerAccountStatus.Active)
            {
                var existing = await _assignments.FindByAccountIdAsync(account.Id, ct);
                if (existing is not null && existing.TenantId == command.ValidatedTenantId)
                    return new ApproveTenantUserResult(account.Id, account.Status, AlreadyActive: true);
                throw new ConflictException("An active producer is already bound to a different tenant."); // 409
            }

            if (account.Status != ProducerAccountStatus.PendingApproval)
                throw new ConflictException(
                    $"Cannot approve a producer in status {account.Status}; it must be PendingApproval (a rejected user must resubmit first)."); // 409 (REQ-6.5)

            var roleCodes = command.RoleCodes
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                .Distinct(StringComparer.Ordinal).ToList();
            if (roleCodes.Count == 0)
                throw new ArgumentException("At least one role must be assigned at approval."); // 400 (REQ-6.2)

            var roleIds = new List<Guid>(roleCodes.Count);
            foreach (var code in roleCodes)
            {
                var role = await _roles.GetByCodeAsync(code, ct);
                if (role is null || role.Status != ProducerRoleStatus.Active)
                    throw new ConflictException($"Role '{code}' is unknown or inactive."); // 409 (REQ-6.5)
                roleIds.Add(role.Id);
            }

            account.Approve(now); // PendingApproval -> Active (REQ-6.2)
            _assignments.Add(ProducerTenantAssignment.Create(account.Id, command.ValidatedTenantId, command.ActingAdminId, now)); // bind tenant (REQ-6.2)
            foreach (var roleId in roleIds)
                _roles.AddAssignment(ProducerRoleAssignment.Create(
                    account.Id, roleId, command.ValidatedTenantId, command.ActingAdminId, now));

            _audit.Append(RegistrationAudit.For(RegistrationAuditAction.Approved, account.Subject, command.CorrelationId, now,
                actorSubject: command.ActingAdminSubject, role: string.Join(", ", roleCodes), tenantId: command.ValidatedTenantId));

            await _unitOfWork.SaveChangesAsync(ct);
            return new ApproveTenantUserResult(account.Id, ProducerAccountStatus.Active, AlreadyActive: false);
        }, cancellationToken));
}

/// <summary>
/// Rejects a PendingApproval producer (REQ-5.1/6). Gated <c>producer.reject</c> at the host. Sets Status Rejected,
/// kills any live sessions of that user (REQ-12.3 — a pending user has none, but defensive), and audits, in ONE
/// pol_admin transaction. A non-PendingApproval target is a 409; an unknown target is a 404 (REQ-22.2).
/// </summary>
public sealed record RejectTenantUserCommand(
    string Subject, string? Reason, string ActingAdminSubject, string CorrelationId) : ICommand<RejectTenantUserResult>;

public sealed record RejectTenantUserResult(Guid TenantUserId, ProducerAccountStatus Status);

public sealed class RejectTenantUserHandler : ICommandHandler<RejectTenantUserCommand, RejectTenantUserResult>
{
    private readonly IProducerAccountRepository _accounts;
    private readonly IProducerSessionStore _sessions;
    private readonly IRegistrationAuditWriter _audit;
    private readonly IProducerUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RejectTenantUserHandler(
        IProducerAccountRepository accounts, IProducerSessionStore sessions, IRegistrationAuditWriter audit,
        IProducerUnitOfWork unitOfWork, IClock clock)
    {
        _accounts = accounts;
        _sessions = sessions;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public ValueTask<RejectTenantUserResult> Handle(RejectTenantUserCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var now = _clock.UtcNow;
            var account = await _accounts.FindBySubjectAsync(command.Subject, ct)
                ?? throw new NotFoundException("The producer registration was not found."); // 404 (REQ-22.2)

            if (account.Status != ProducerAccountStatus.PendingApproval)
                throw new ConflictException($"Cannot reject a producer in status {account.Status}; it must be PendingApproval."); // 409

            account.Reject(now); // PendingApproval -> Rejected (REQ-5.1)
            await _sessions.RevokeAllForUserAsync(account.Id, ct); // kill any live sessions (REQ-12.3)

            _audit.Append(RegistrationAudit.For(RegistrationAuditAction.Rejected, account.Subject, command.CorrelationId, now,
                actorSubject: command.ActingAdminSubject, reason: NormalizeReason(command.Reason))); // record the rationale (REQ-5.1)

            await _unitOfWork.SaveChangesAsync(ct);
            return new RejectTenantUserResult(account.Id, ProducerAccountStatus.Rejected);
        }, cancellationToken));

    // Blank -> NULL (no rationale given); trim + cap to the audit column width (REQ-5.1).
    private static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;
        var trimmed = reason.Trim();
        return trimmed.Length <= 1024 ? trimmed : trimmed[..1024];
    }
}
