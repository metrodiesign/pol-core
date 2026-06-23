using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.UnassignTenant;

/// <summary>A Super removes a tenant assignment from a Scoped admin (REQ-4.2): a hard delete of the
/// <see cref="AdminTenantAssignment"/> row (the migration grants DELETE on the table to pol_admin) plus an
/// <c>unassign-tenant</c> audit. An unknown assignment -> 404. Super-only at the host.</summary>
public sealed record UnassignTenantCommand(Guid AdminId, Guid TenantId, Guid ActingAdminId, string CorrelationId)
    : ICommand<UnassignTenantResult>;

public sealed record UnassignTenantResult(Guid AdminId, Guid TenantId);

public sealed class UnassignTenantHandler : ICommandHandler<UnassignTenantCommand, UnassignTenantResult>
{
    private readonly IAdminAccountRepository _admins;
    private readonly IAdminAccountAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public UnassignTenantHandler(
        IAdminAccountRepository admins,
        IAdminAccountAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<UnassignTenantResult> Handle(UnassignTenantCommand command, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var assignment = await _admins.GetAssignmentAsync(command.AdminId, command.TenantId, ct)
                ?? throw new NotFoundException("That tenant is not assigned to this admin.");

            _admins.RemoveAssignment(assignment);
            _audit.Append(AdminAccountAudit.For(
                AdminAuditAction.UnassignTenant, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.AdminId, tenantId: command.TenantId));
            await _unitOfWork.SaveChangesAsync(ct);
            return assignment.Id;
        }, cancellationToken);

        return new UnassignTenantResult(command.AdminId, command.TenantId);
    }
}
