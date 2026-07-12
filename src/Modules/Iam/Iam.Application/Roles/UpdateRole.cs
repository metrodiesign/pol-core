using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Iam.Application.Roles;

/// <summary>Updates a role's name/description/color/status/permissions on either side (REQ-2.4/6.1/6.2).
/// <c>Code</c> is immutable (never read here). Invisible to <see cref="RoleSideContext"/> -> 404 (REQ-6.3, no
/// existence leak). A Merchant-side caller mutating a role it does not own — INCLUDING a shared seed role it
/// can see but not edit — is a 409 (REQ-3.8); the Platform side has no equivalent case (every Platform role
/// IS the shared bucket, REQ-3.9). A permission key outside the catalog, or one whose side does not match this
/// role's own <see cref="Role.Scope"/>, is 400 (REQ-6.6). Deactivating a seed anchor is 409 (REQ-2.4). Gated
/// <c>user.roles</c> (admin) / <c>roles.manage</c> (merchant) at the host.</summary>
public sealed record UpdateRoleCommand(
    RoleSideContext Context, string Code, string Name, string? Description, string? Color, RoleStatus Status,
    IReadOnlyList<string> PermissionKeys, string CorrelationId) : ICommand<RoleListItem>;

public sealed class UpdateRoleHandler : ICommandHandler<UpdateRoleCommand, RoleListItem>
{
    private readonly IRoleStore _roles;
    private readonly IRoleAssignmentCounter _counter;
    private readonly IRoleAuditSink _audit;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateRoleHandler(
        IRoleStore roles, IRoleAssignmentCounter counter, IRoleAuditSink audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork)
    {
        _roles = roles;
        _counter = counter;
        _audit = audit;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<RoleListItem> Handle(UpdateRoleCommand command, CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var role = await _roles.GetByCodeAsync(command.Context, command.Code, ct)
                ?? throw new NotFoundException($"Role '{command.Code}' was not found.");

            // Merchant-only ownership guard (REQ-3.8): a role this context can SEE but does not OWN — the
            // shared seed is the only case today, since the visible set otherwise contains only own-merchant rows.
            if (command.Context.Scope == Scope.Merchant && role.MerchantId != command.Context.MerchantId)
                throw new ConflictException($"Role '{command.Code}' cannot be modified by this merchant.");

            // Recovery-anchor guard (REQ-2.4): a clean 409 before the domain backstop would throw.
            if (role.IsSeedAnchor && command.Status == RoleStatus.Inactive)
                throw new ConflictException($"The {role.Code} role cannot be deactivated.");

            role.Rename(command.Name);
            role.SetDescription(command.Description);
            role.SetColor(command.Color);
            role.SetPermissions(command.PermissionKeys, Keys.KeySide);
            if (command.Status == RoleStatus.Active)
                role.Activate();
            else
                role.Deactivate();

            _audit.RoleUpdated(role.Id, command.CorrelationId);
            await _unitOfWork.SaveChangesAsync(ct);

            var userCount = await _counter.CountAsync(command.Context, role.Id, ct);
            return new RoleListItem(role.Id, role.Code, role.Name, role.Description, role.Color, role.Status,
                Shared: role.MerchantId is null, [.. role.PermissionKeys], userCount);
        }, cancellationToken);
}
