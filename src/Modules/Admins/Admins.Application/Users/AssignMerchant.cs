using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>A Super assigns one merchant to a Scoped admin (REQ-4.1). The target admin must exist and be Scoped;
/// the merchant must exist and be Active (validated via <see cref="IAdminMerchantDirectory"/> -> 409 if not,
/// REQ-4.3); a duplicate <c>(PlatformUserId, MerchantId)</c> is rejected (409, REQ-4.4). Super-only at the host.</summary>
public sealed record AssignMerchantCommand(Guid AdminId, Guid MerchantId, Guid ActingAdminId, string CorrelationId)
    : ICommand<AssignMerchantResult>;

public sealed record AssignMerchantResult(Guid AssignmentId);

public sealed class AssignMerchantHandler : ICommandHandler<AssignMerchantCommand, AssignMerchantResult>
{
    private readonly IUserRepository _admins;
    private readonly IAdminMerchantDirectory _merchants;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public AssignMerchantHandler(
        IUserRepository admins,
        IAdminMerchantDirectory merchants,
        IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _merchants = merchants;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<AssignMerchantResult> Handle(AssignMerchantCommand command, CancellationToken cancellationToken)
    {
        // The selected merchant must exist AND be active — reuse the same directory the approve path trusts.
        if (!await _merchants.IsActiveMerchantAsync(command.MerchantId, cancellationToken))
            throw new ConflictException("The selected merchant does not exist or is not active.");

        var assignmentId = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var admin = await _admins.GetByIdAsync(command.AdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");
            if (admin.Tier != Tier.Scoped)
                throw new ConflictException("Merchant assignments apply only to Scoped admins; a Super already has unrestricted reach.");

            if (await _admins.GetAssignmentAsync(command.AdminId, command.MerchantId, ct) is not null)
                throw new ConflictException("That merchant is already assigned to this admin.");

            var assignment = MerchantAccess.Create(command.AdminId, command.MerchantId, command.ActingAdminId, _clock.UtcNow);
            _admins.AddAssignment(assignment);
            _audit.Append(Audit.For(
                AuditAction.AssignMerchant, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.AdminId, merchantId: command.MerchantId));
            await _unitOfWork.SaveChangesAsync(ct);
            return assignment.Id;
        }, cancellationToken);

        return new AssignMerchantResult(assignmentId);
    }
}
