using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.ReactivateAdmin;

/// <summary>A Super restores a suspended admin (admin-account-management REQ-3). On the Suspended->Active
/// transition ALL of the target's sessions are revoked first so a cookie stolen before suspension cannot resume
/// (REQ-3.5); the already-Active case is an idempotent no-revoke (REQ-3.6). The account load, the revoke, the
/// status flip, and the audit run in ONE keyed "admin" transaction so they commit or roll back together
/// (REQ-3.2). Unknown target -> 404. Super-only at the host.</summary>
public sealed record ReactivateAdminCommand(Guid TargetAdminId, Guid ActingAdminId, string CorrelationId)
    : ICommand<ReactivateAdminResult>;

public sealed record ReactivateAdminResult(Guid AdminId, string Status);

public sealed class ReactivateAdminHandler : ICommandHandler<ReactivateAdminCommand, ReactivateAdminResult>
{
    private readonly IPlatformUserRepository _admins;
    private readonly IPlatformUserSessionStore _sessions;
    private readonly IPlatformUserAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ReactivateAdminHandler(
        IPlatformUserRepository admins,
        IPlatformUserSessionStore sessions,
        IPlatformUserAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _sessions = sessions;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ReactivateAdminResult> Handle(ReactivateAdminCommand command, CancellationToken cancellationToken)
    {
        var status = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Load INSIDE the lambda so an execution-strategy retry re-loads a fresh entity (mirrors SuspendAdmin).
            var admin = await _admins.GetByIdAsync(command.TargetAdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");

            bool wasSuspended = admin.Status == AdminStatus.Suspended;
            admin.Reactivate();
            if (wasSuspended)
                // Set-based ExecuteUpdate enrolled in this transaction (fresh-login guarantee, REQ-3.5).
                await _sessions.RevokeAllForAdminAsync(command.TargetAdminId, ct);

            // Audit every accepted call, including the idempotent already-Active case (REQ-3.2/3.3).
            _audit.Append(PlatformUserAudit.For(
                AdminAuditAction.Reactivate, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.TargetAdminId));
            await _unitOfWork.SaveChangesAsync(ct);
            return admin.Status;
        }, cancellationToken);

        return new ReactivateAdminResult(command.TargetAdminId, status.ToString());
    }
}
