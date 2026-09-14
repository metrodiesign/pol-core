using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>A Super restores a suspended admin (admin-account-management REQ-3). The already-Active case is an
/// idempotent no-op apart from the audit (REQ-3.6). The account load, the status flip, and the audit run in ONE
/// keyed "admin" transaction so they commit or roll back together (REQ-3.2). A platform token issued before the
/// suspension stays rejected until the account's authorization version matches again (the token handler re-checks
/// status and version on every request). Unknown target -> 404. Super-only at the host.</summary>
public sealed record ReactivateCommand(Guid TargetAdminId, Guid ActingAdminId, string CorrelationId, long ExpectedVersion)
    : ICommand<ReactivateResult>;

public sealed record ReactivateResult(Guid AdminId, string Status, long Version);

public sealed class ReactivateHandler : ICommandHandler<ReactivateCommand, ReactivateResult>
{
    private readonly IUserRepository _admins;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ReactivateHandler(
        IUserRepository admins,
        IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ReactivateResult> Handle(ReactivateCommand command, CancellationToken cancellationToken)
    {
        var state = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Load INSIDE the lambda so an execution-strategy retry re-loads a fresh entity (mirrors SuspendAdmin).
            var admin = await _admins.GetByIdAsync(command.TargetAdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");
            if (admin.Version != command.ExpectedVersion)
                throw new ConflictException("The admin account changed after it was loaded.", "state_conflict");

            admin.Reactivate();

            // Audit every accepted call, including the idempotent already-Active case (REQ-3.2/3.3).
            _audit.Append(Audit.For(
                AuditAction.Reactivate, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.TargetAdminId));
            await _unitOfWork.SaveChangesAsync(ct);
            return (admin.Status, admin.Version);
        }, cancellationToken);

        return new ReactivateResult(command.TargetAdminId, state.Status.ToString(), state.Version);
    }
}
