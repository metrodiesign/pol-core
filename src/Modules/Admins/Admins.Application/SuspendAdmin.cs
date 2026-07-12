using Admins.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.SuspendAdmin;

/// <summary>A Super suspends another admin (REQ-8). The domain blocks self-suspension (REQ-8.2) as a
/// defense-in-depth invariant; the host also rejects it up front with a 403. An unknown target -> 404.
/// Super-only at the host.</summary>
public sealed record SuspendAdminCommand(Guid TargetAdminId, Guid ActingAdminId, string CorrelationId)
    : ICommand<SuspendAdminResult>;

public sealed record SuspendAdminResult(Guid AdminId, string Status);

public sealed class SuspendAdminHandler : ICommandHandler<SuspendAdminCommand, SuspendAdminResult>
{
    private readonly IPlatformUserRepository _admins;
    private readonly IPlatformUserAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SuspendAdminHandler(
        IPlatformUserRepository admins,
        IPlatformUserAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<SuspendAdminResult> Handle(SuspendAdminCommand command, CancellationToken cancellationToken)
    {
        var status = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var admin = await _admins.GetByIdAsync(command.TargetAdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");

            admin.Suspend(command.ActingAdminId); // throws on self-suspend (REQ-8.2)
            _audit.Append(PlatformUserAudit.For(
                AdminAuditAction.Suspend, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.TargetAdminId));
            await _unitOfWork.SaveChangesAsync(ct);
            return admin.Status;
        }, cancellationToken);

        return new SuspendAdminResult(command.TargetAdminId, status.ToString());
    }
}
