using Admins.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.DeleteRole;

/// <summary>Deletes a role (REQ-2). A role with ≥1 bound user is undeletable (409, REQ-4.4); unknown role -> 404.
/// Cascade removes the role's permission grants. Gated <c>user.roles</c> at the host.</summary>
public sealed record DeleteRoleCommand(string Code, Guid ActingAdminId, string CorrelationId)
    : ICommand<DeleteRoleResult>;

public sealed record DeleteRoleResult(string Code);

public sealed class DeleteRoleHandler : ICommandHandler<DeleteRoleCommand, DeleteRoleResult>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IPlatformUserAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public DeleteRoleHandler(
        IAdminRoleRepository roles,
        IPlatformUserAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<DeleteRoleResult> Handle(DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var role = await _roles.GetByCodeAsync(command.Code, ct)
                ?? throw new NotFoundException($"Role '{command.Code}' was not found.");

            // Recovery-anchor guard (REQ-8.3): deleting the seed would create the same Super-tier lockout that
            // the deactivation guard prevents. A role with bound users is also undeletable (REQ-4.4).
            if (role.IsSuperAdminSeed)
                throw new ConflictException("The super_admin role cannot be deleted.");
            if (await _roles.CountAssignmentsForRoleAsync(role.Id, ct) > 0)
                throw new ConflictException("A role with bound users cannot be deleted.");

            _roles.Remove(role);
            _audit.Append(PlatformUserAudit.For(
                AdminAuditAction.RoleDeleted, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetRoleId: role.Id));
            await _unitOfWork.SaveChangesAsync(ct);
            return true; // transaction payload is unused — the result is built from command.Code below
        }, cancellationToken);

        return new DeleteRoleResult(command.Code);
    }
}
