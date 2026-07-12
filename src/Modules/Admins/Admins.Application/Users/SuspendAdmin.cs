using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>A Super suspends another admin (REQ-8). The domain blocks self-suspension (REQ-8.2) as a
/// defense-in-depth invariant; the host also rejects it up front with a 403. An unknown target -> 404.
/// Super-only at the host.</summary>
public sealed record SuspendCommand(Guid TargetAdminId, Guid ActingAdminId, string CorrelationId)
    : ICommand<SuspendResult>;

public sealed record SuspendResult(Guid AdminId, string Status);

public sealed class SuspendHandler : ICommandHandler<SuspendCommand, SuspendResult>
{
    private readonly IUserRepository _admins;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SuspendHandler(
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

    public async ValueTask<SuspendResult> Handle(SuspendCommand command, CancellationToken cancellationToken)
    {
        var status = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var admin = await _admins.GetByIdAsync(command.TargetAdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");

            admin.Suspend(command.ActingAdminId); // throws on self-suspend (REQ-8.2)
            _audit.Append(Audit.For(
                AuditAction.Suspend, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.TargetAdminId));
            await _unitOfWork.SaveChangesAsync(ct);
            return admin.Status;
        }, cancellationToken);

        return new SuspendResult(command.TargetAdminId, status.ToString());
    }
}
