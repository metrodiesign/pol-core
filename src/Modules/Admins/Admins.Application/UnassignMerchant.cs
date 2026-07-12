using Admins.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.UnassignMerchant;

/// <summary>A Super removes a merchant assignment from a Scoped admin (REQ-4.2): a hard delete of the
/// <see cref="PlatformMerchantAccess"/> row (the migration grants DELETE on the table to pol_admin) plus an
/// <c>unassign-merchant</c> audit. An unknown assignment -> 404. Super-only at the host.</summary>
public sealed record UnassignMerchantCommand(Guid AdminId, Guid MerchantId, Guid ActingAdminId, string CorrelationId)
    : ICommand<UnassignMerchantResult>;

public sealed record UnassignMerchantResult(Guid AdminId, Guid MerchantId);

public sealed class UnassignMerchantHandler : ICommandHandler<UnassignMerchantCommand, UnassignMerchantResult>
{
    private readonly IPlatformUserRepository _admins;
    private readonly IPlatformUserAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public UnassignMerchantHandler(
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

    public async ValueTask<UnassignMerchantResult> Handle(UnassignMerchantCommand command, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var assignment = await _admins.GetAssignmentAsync(command.AdminId, command.MerchantId, ct)
                ?? throw new NotFoundException("That merchant is not assigned to this admin.");

            _admins.RemoveAssignment(assignment);
            _audit.Append(PlatformUserAudit.For(
                AdminAuditAction.UnassignMerchant, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.AdminId, merchantId: command.MerchantId));
            await _unitOfWork.SaveChangesAsync(ct);
            return assignment.Id;
        }, cancellationToken);

        return new UnassignMerchantResult(command.AdminId, command.MerchantId);
    }
}
