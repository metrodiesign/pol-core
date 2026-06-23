using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.AssignTenant;

/// <summary>A Super assigns one tenant to a Scoped admin (REQ-4.1). The target admin must exist and be Scoped;
/// the tenant must exist and be Active (validated via <see cref="IAdminTenantDirectory"/> -> 409 if not,
/// REQ-4.3); a duplicate <c>(AdminAccountId, TenantId)</c> is rejected (409, REQ-4.4). Super-only at the host.</summary>
public sealed record AssignTenantCommand(Guid AdminId, Guid TenantId, Guid ActingAdminId, string CorrelationId)
    : ICommand<AssignTenantResult>;

public sealed record AssignTenantResult(Guid AssignmentId);

public sealed class AssignTenantHandler : ICommandHandler<AssignTenantCommand, AssignTenantResult>
{
    private readonly IAdminAccountRepository _admins;
    private readonly IAdminTenantDirectory _tenants;
    private readonly IAdminAccountAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public AssignTenantHandler(
        IAdminAccountRepository admins,
        IAdminTenantDirectory tenants,
        IAdminAccountAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _tenants = tenants;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<AssignTenantResult> Handle(AssignTenantCommand command, CancellationToken cancellationToken)
    {
        // The selected tenant must exist AND be active — reuse the same directory the approve path trusts.
        if (!await _tenants.IsActiveTenantAsync(command.TenantId, cancellationToken))
            throw new ConflictException("The selected tenant does not exist or is not active.");

        var assignmentId = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var admin = await _admins.GetByIdAsync(command.AdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");
            if (admin.Tier != AdminTier.Scoped)
                throw new ConflictException("Tenant assignments apply only to Scoped admins; a Super already has unrestricted reach.");

            if (await _admins.GetAssignmentAsync(command.AdminId, command.TenantId, ct) is not null)
                throw new ConflictException("That tenant is already assigned to this admin.");

            var assignment = AdminTenantAssignment.Create(command.AdminId, command.TenantId, command.ActingAdminId, _clock.UtcNow);
            _admins.AddAssignment(assignment);
            _audit.Append(AdminAccountAudit.For(
                AdminAuditAction.AssignTenant, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.AdminId, tenantId: command.TenantId));
            await _unitOfWork.SaveChangesAsync(ct);
            return assignment.Id;
        }, cancellationToken);

        return new AssignTenantResult(assignmentId);
    }
}
