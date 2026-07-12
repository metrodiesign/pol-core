using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Roles;

/// <summary>Creates a role (REQ-2). A duplicate <see cref="Code"/> is rejected (pre-check 409; the unique index is
/// the race-safe backstop). A permission key outside the catalog is rejected by the aggregate (ArgumentException ->
/// 400, REQ-3.3). Gated <c>user.roles</c> at the host.</summary>
public sealed record CreateRoleCommand(
    string Code, string Name, string? Description, string? Color, RoleStatus Status,
    IReadOnlyList<string> PermissionKeys, Guid ActingAdminId, string CorrelationId)
    : ICommand<RoleListItem>;

public sealed class CreateRoleHandler : ICommandHandler<CreateRoleCommand, RoleListItem>
{
    private readonly IRoleRepository _roles;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateRoleHandler(
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

    public async ValueTask<RoleListItem> Handle(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var code = command.Code?.Trim() ?? string.Empty;
            if (await _roles.CodeExistsAsync(code, ct))
                throw new ConflictException($"A role with code '{code}' already exists.");

            var catalog = await _roles.ListCatalogKeysAsync(ct);
            var role = Role.Create(code, command.Name, command.Description, command.Color,
                command.Status, command.PermissionKeys, catalog);
            _roles.Add(role);
            _audit.Append(Audit.For(
                AuditAction.RoleCreated, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetRoleId: role.Id));
            await _unitOfWork.SaveChangesAsync(ct);

            return new RoleListItem(role.Code, role.Name, role.Description, role.Color, role.Status,
                [.. role.PermissionKeys], UserCount: 0);
        }, cancellationToken);
    }
}
