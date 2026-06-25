using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.CreateRole;

/// <summary>Creates a role (REQ-2). A duplicate <see cref="Code"/> is rejected (pre-check 409; the unique index is
/// the race-safe backstop). A permission key outside the catalog is rejected by the aggregate (ArgumentException ->
/// 400, REQ-3.3). Gated <c>user.roles</c> at the host.</summary>
public sealed record CreateRoleCommand(
    string Code, string Name, string? Description, string? Color, AdminRoleStatus Status,
    IReadOnlyList<string> PermissionKeys, Guid ActingAdminId, string CorrelationId)
    : ICommand<AdminRoleListItem>;

public sealed class CreateRoleHandler : ICommandHandler<CreateRoleCommand, AdminRoleListItem>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAdminAccountAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateRoleHandler(
        IAdminRoleRepository roles,
        IAdminAccountAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<AdminRoleListItem> Handle(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var code = command.Code?.Trim() ?? string.Empty;
            if (await _roles.CodeExistsAsync(code, ct))
                throw new ConflictException($"A role with code '{code}' already exists.");

            var catalog = await _roles.ListCatalogKeysAsync(ct);
            var role = AdminRole.Create(code, command.Name, command.Description, command.Color,
                command.Status, command.PermissionKeys, catalog);
            _roles.Add(role);
            _audit.Append(AdminAccountAudit.For(
                AdminAuditAction.RoleCreated, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetRoleId: role.Id));
            await _unitOfWork.SaveChangesAsync(ct);

            return new AdminRoleListItem(role.Code, role.Name, role.Description, role.Color, role.Status,
                [.. role.PermissionKeys], UserCount: 0);
        }, cancellationToken);
    }
}
