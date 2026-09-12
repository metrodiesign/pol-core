using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Iam.Application.Roles;

/// <summary>Creates a role on either side (REQ-2/6.1/6.2), replacing the two duplicated
/// <c>Admins.Application.Roles.CreateRoleHandler</c> / <c>Merchants.Application.Users.Roles.CreateHandler</c>.
/// A duplicate <see cref="Code"/> within <see cref="RoleSideContext.Scope"/>'s visible set (or the shared NULL
/// bucket — critique P2-5) is rejected (pre-check 409; the UNIQUE index is the race-safe backstop, translated
/// to 409 by <c>EfUnitOfWork</c>). A permission key outside the catalog, or one whose side does not match
/// <see cref="RoleSideContext.Scope"/>, is rejected by the aggregate (ArgumentException -> 400, REQ-2.6/6.6).
/// <see cref="RoleSideContext.Scope"/>/<see cref="RoleSideContext.MerchantId"/> come from the caller, never the
/// wire body (REQ-3.7/3.9). Gated <c>user.roles</c> (admin) / <c>roles.manage</c> (merchant) at the host.</summary>
public sealed record CreateRoleCommand(
    RoleSideContext Context, string Code, string Name, string? Description, string? Color, RoleStatus Status,
    IReadOnlyList<string> PermissionKeys, string CorrelationId) : ICommand<RoleListItem>;

public sealed class CreateRoleHandler : ICommandHandler<CreateRoleCommand, RoleListItem>
{
    private readonly IRoleStore _roles;
    private readonly IRoleAuditSink _audit;
    private readonly IUnitOfWork _unitOfWork;

    public CreateRoleHandler(
        IRoleStore roles, IRoleAuditSink audit, [FromKeyedServices("admin")] IUnitOfWork unitOfWork)
    {
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<RoleListItem> Handle(CreateRoleCommand command, CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var code = command.Code?.Trim() ?? string.Empty;
            if (await _roles.CodeExistsAsync(command.Context, code, ct))
                throw new ConflictException($"A role with code '{code}' already exists.");

            var role = Role.Create(code, command.Name, command.Description, command.Color, command.Status,
                command.Context.Scope, command.Context.MerchantId, command.PermissionKeys, Keys.KeySide);
            _roles.Add(role);
            _audit.RoleCreated(role.Id, command.CorrelationId);
            await _unitOfWork.SaveChangesAsync(ct);

            return new RoleListItem(role.Id, role.Code, role.Name, role.Description, role.Color, role.Status,
                Shared: role.MerchantId is null, [.. role.PermissionKeys], role.Version, UserCount: 0);
        }, cancellationToken);
}
