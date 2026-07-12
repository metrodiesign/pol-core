using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Roles;

/// <summary>Updates a role's name/description/color/status/permissions (REQ-2.4). <c>Code</c> is immutable (never
/// read here). Unknown target -> 404; a permission key outside the catalog -> 400; deactivating the
/// <c>super_admin</c> seed -> 409 (REQ-8.3). Gated <c>user.roles</c> at the host.</summary>
public sealed record UpdateRoleCommand(
    string Code, string Name, string? Description, string? Color, RoleStatus Status,
    IReadOnlyList<string> PermissionKeys, Guid ActingAdminId, string CorrelationId)
    : ICommand<RoleListItem>;

public sealed class UpdateRoleHandler : ICommandHandler<UpdateRoleCommand, RoleListItem>
{
    private readonly IRoleRepository _roles;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public UpdateRoleHandler(
        IRoleRepository roles,
        IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<RoleListItem> Handle(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var role = await _roles.GetByCodeAsync(command.Code, ct)
                ?? throw new NotFoundException($"Role '{command.Code}' was not found.");

            // Recovery-anchor guard (REQ-8.3): a clean 409 before the domain backstop would throw.
            if (role.IsSuperAdminSeed && command.Status == RoleStatus.Inactive)
                throw new ConflictException("The super_admin role cannot be deactivated.");

            var catalog = await _roles.ListCatalogKeysAsync(ct);
            role.Rename(command.Name);
            role.SetDescription(command.Description);
            role.SetColor(command.Color);
            role.SetPermissions(command.PermissionKeys, catalog);
            if (command.Status == RoleStatus.Active)
                role.Activate();
            else
                role.Deactivate();

            _audit.Append(Audit.For(
                AuditAction.RoleUpdated, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetRoleId: role.Id));
            await _unitOfWork.SaveChangesAsync(ct);

            var userCount = await _roles.CountAssignmentsForRoleAsync(role.Id, ct);
            return new RoleListItem(role.Code, role.Name, role.Description, role.Color, role.Status,
                [.. role.PermissionKeys], userCount);
        }, cancellationToken);
    }
}
