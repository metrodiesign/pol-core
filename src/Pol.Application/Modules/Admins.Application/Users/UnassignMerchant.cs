using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>A Super removes a merchant assignment from a Scoped admin (REQ-4.2): a hard delete of the
/// <see cref="MerchantAccess"/> row (the migration grants DELETE on the table to pol_admin) plus an
/// <c>unassign-merchant</c> audit. An unknown assignment -> 404. Super-only at the host.</summary>
public sealed record UnassignMerchantCommand(
    Guid AdminId, Guid MerchantId, Guid ActingAdminId, string CorrelationId, long ExpectedVersion)
    : ICommand<UnassignMerchantResult>;

public sealed record UnassignMerchantResult(Guid AdminId, Guid MerchantId, long Version);

public sealed class UnassignMerchantHandler : ICommandHandler<UnassignMerchantCommand, UnassignMerchantResult>
{
    private readonly IUserRepository _admins;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public UnassignMerchantHandler(
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

    public async ValueTask<UnassignMerchantResult> Handle(UnassignMerchantCommand command, CancellationToken cancellationToken)
    {
        var version = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var admin = await _admins.GetByIdAsync(command.AdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");
            if (admin.Version != command.ExpectedVersion)
                throw new ConflictException("The admin account changed after it was loaded.", "state_conflict");
            var assignment = await _admins.GetAssignmentAsync(command.AdminId, command.MerchantId, ct)
                ?? throw new NotFoundException("That merchant is not assigned to this admin.");

            _admins.RemoveAssignment(assignment);
            admin.BumpAuthorizationVersion();
            admin.BumpResourceVersion();
            _audit.Append(Audit.For(
                AuditAction.UnassignMerchant, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.AdminId, merchantId: command.MerchantId));
            await _unitOfWork.SaveChangesAsync(ct);
            return admin.Version;
        }, cancellationToken);

        return new UnassignMerchantResult(command.AdminId, command.MerchantId, version);
    }
}
