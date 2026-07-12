using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain;

namespace Merchants.Application;

/// <summary>
/// Approves a merchant user onto a merchant the admin already validated (REQ-6). The Admin permission check
/// (<c>merchant_user.approve</c>) AND the accessible-merchant floor run at the HOST before dispatch (critique B3); this
/// command receives an ALREADY-VALIDATED merchant id and lives in Merchants.Application with NO Admin import. It runs
/// in ONE pol_admin transaction: validate the assigned roles exist + are Active (REQ-6.5), bind the merchant, set the
/// role assignments, flip the user Active, and audit. Idempotent: an already-Active target is a no-op success
/// (REQ-6.4), enforced by <see cref="MerchantUser.Approve"/> itself. A non-PendingApproval target (Rejected/Suspended —
/// must resubmit first) is a 409 (REQ-6.5).
/// </summary>
public sealed record ApproveMerchantUserCommand(
    string Subject,
    Guid ValidatedMerchantId,
    IReadOnlyList<string> RoleCodes,
    string ActingAdminSubject,
    Guid ActingAdminId,
    string CorrelationId) : ICommand<ApproveMerchantUserResult>;

public sealed record ApproveMerchantUserResult(Guid MerchantUserId, MerchantUserStatus Status, bool AlreadyActive);

public sealed class ApproveMerchantUserHandler : ICommandHandler<ApproveMerchantUserCommand, ApproveMerchantUserResult>
{
    private readonly IMerchantUserRepository _accounts;
    private readonly IMerchantUserRoleRepository _roles;
    private readonly IRegistrationAuditWriter _audit;
    private readonly IMerchantsUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ApproveMerchantUserHandler(
        IMerchantUserRepository accounts, IMerchantUserRoleRepository roles, IRegistrationAuditWriter audit,
        IMerchantsUnitOfWork unitOfWork, IClock clock)
    {
        _accounts = accounts;
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public ValueTask<ApproveMerchantUserResult> Handle(ApproveMerchantUserCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var now = _clock.UtcNow;
            var account = await _accounts.FindBySubjectAsync(command.Subject, ct)
                ?? throw new NotFoundException("The merchant-user registration was not found."); // 404 (REQ-22.2)

            // Idempotent no-op for an already-Active target ONLY when bound to the same merchant (REQ-6.4); re-approving
            // onto a different merchant is rejected. MerchantUser.Approve enforces both — a different-merchant re-approve
            // throws InvalidOperationException (mapped to 409 by ProblemDetailsExceptionHandler), a same-merchant
            // re-approve is a silent no-op — so this branch just detects which one happened for the result payload.
            if (account.Status == MerchantUserStatus.Active)
            {
                account.Approve(command.ValidatedMerchantId, now);
                return new ApproveMerchantUserResult(account.Id, account.Status, AlreadyActive: true);
            }

            if (account.Status != MerchantUserStatus.PendingApproval)
                throw new ConflictException(
                    $"Cannot approve a merchant user in status {account.Status}; it must be PendingApproval (a rejected user must resubmit first)."); // 409 (REQ-6.5)

            var roleCodes = command.RoleCodes
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                .Distinct(StringComparer.Ordinal).ToList();
            if (roleCodes.Count == 0)
                throw new ArgumentException("At least one role must be assigned at approval."); // 400 (REQ-6.2)

            var roleIds = new List<Guid>(roleCodes.Count);
            foreach (var code in roleCodes)
            {
                var role = await _roles.GetByCodeAsync(code, ct);
                if (role is null || role.Status != MerchantUserRoleStatus.Active)
                    throw new ConflictException($"Role '{code}' is unknown or inactive."); // 409 (REQ-6.5)
                roleIds.Add(role.Id);
            }

            account.Approve(command.ValidatedMerchantId, now); // PendingApproval -> Active, sets MerchantId (REQ-6.2/9.2)
            foreach (var roleId in roleIds)
                _roles.AddAssignment(MerchantUserRoleAssignment.Create(
                    account.Id, roleId, command.ValidatedMerchantId, command.ActingAdminId, now));

            _audit.Append(RegistrationAudit.For(RegistrationAuditAction.Approved, account.Subject, command.CorrelationId, now,
                actorSubject: command.ActingAdminSubject, role: string.Join(", ", roleCodes), merchantId: command.ValidatedMerchantId));

            await _unitOfWork.SaveChangesAsync(ct);
            return new ApproveMerchantUserResult(account.Id, MerchantUserStatus.Active, AlreadyActive: false);
        }, cancellationToken));
}

/// <summary>
/// Rejects a PendingApproval merchant user (REQ-5.1/6). Gated <c>merchant_user.reject</c> at the host. Sets Status Rejected,
/// kills any live sessions of that user (REQ-12.3 — a pending user has none, but defensive), and audits, in ONE
/// pol_admin transaction. A non-PendingApproval target is a 409; an unknown target is a 404 (REQ-22.2).
/// </summary>
public sealed record RejectMerchantUserCommand(
    string Subject, string? Reason, string ActingAdminSubject, string CorrelationId) : ICommand<RejectMerchantUserResult>;

public sealed record RejectMerchantUserResult(Guid MerchantUserId, MerchantUserStatus Status);

public sealed class RejectMerchantUserHandler : ICommandHandler<RejectMerchantUserCommand, RejectMerchantUserResult>
{
    private readonly IMerchantUserRepository _accounts;
    private readonly IMerchantUserSessionStore _sessions;
    private readonly IRegistrationAuditWriter _audit;
    private readonly IMerchantsUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RejectMerchantUserHandler(
        IMerchantUserRepository accounts, IMerchantUserSessionStore sessions, IRegistrationAuditWriter audit,
        IMerchantsUnitOfWork unitOfWork, IClock clock)
    {
        _accounts = accounts;
        _sessions = sessions;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public ValueTask<RejectMerchantUserResult> Handle(RejectMerchantUserCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var now = _clock.UtcNow;
            var account = await _accounts.FindBySubjectAsync(command.Subject, ct)
                ?? throw new NotFoundException("The merchant-user registration was not found."); // 404 (REQ-22.2)

            if (account.Status != MerchantUserStatus.PendingApproval)
                throw new ConflictException($"Cannot reject a merchant user in status {account.Status}; it must be PendingApproval."); // 409

            account.Reject(now); // PendingApproval -> Rejected (REQ-5.1)
            await _sessions.RevokeAllForUserAsync(account.Id, ct); // kill any live sessions (REQ-12.3)

            _audit.Append(RegistrationAudit.For(RegistrationAuditAction.Rejected, account.Subject, command.CorrelationId, now,
                actorSubject: command.ActingAdminSubject, reason: NormalizeReason(command.Reason))); // record the rationale (REQ-5.1)

            await _unitOfWork.SaveChangesAsync(ct);
            return new RejectMerchantUserResult(account.Id, MerchantUserStatus.Rejected);
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
