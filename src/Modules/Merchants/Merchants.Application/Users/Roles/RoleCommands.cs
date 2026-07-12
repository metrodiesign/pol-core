using BuildingBlocks.Application;
using Mediator;
using Merchants.Application.Users;
using Merchants.Domain.Users.Roles;

namespace Merchants.Application.Users.Roles;

/// <summary>Creates a merchant-user role (REQ-16). A duplicate <see cref="Code"/> -> 409 (pre-check; the unique index is
/// the race-safe backstop). A permission key outside the catalog is rejected by the aggregate (ArgumentException ->
/// 400, REQ-16.2). Gated <c>merchant_user.roles.manage</c> at the host (S8).</summary>
// ponytail: DUPLICATE-shaped of Admins.Application.CreateRole (no audit — merchant-user role CRUD is not in REQ-21) — deliberate.
public sealed record CreateCommand(
    string Code, string Name, string? Description, string? Color, RoleStatus Status,
    IReadOnlyList<string> PermissionKeys) : ICommand<RoleListItem>;

public sealed class CreateHandler : ICommandHandler<CreateCommand, RoleListItem>
{
    private readonly IRoleRepository _roles;
    private readonly IUserUnitOfWork _unitOfWork;

    public CreateHandler(IRoleRepository roles, IUserUnitOfWork unitOfWork)
    {
        _roles = roles;
        _unitOfWork = unitOfWork;
    }

    public ValueTask<RoleListItem> Handle(CreateCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var code = command.Code?.Trim() ?? string.Empty;
            if (await _roles.CodeExistsAsync(code, ct))
                throw new ConflictException($"A role with code '{code}' already exists.");

            var catalog = await _roles.ListCatalogKeysAsync(ct);
            var role = Role.Create(code, command.Name, command.Description, command.Color,
                command.Status, command.PermissionKeys, catalog);
            _roles.Add(role);
            await _unitOfWork.SaveChangesAsync(ct);

            return new RoleListItem(role.Code, role.Name, role.Description, role.Color, role.Status,
                [.. role.PermissionKeys], UserCount: 0);
        }, cancellationToken));
}

/// <summary>Updates a merchant-user role's name/description/color/status/permissions (REQ-16.1). <c>Code</c> is immutable.
/// Unknown target -> 404; a key outside the catalog -> 400; deactivating the <c>merchant_owner</c> anchor -> 409
/// (REQ-16.5). Gated <c>merchant_user.roles.manage</c> at the host.</summary>
public sealed record UpdateCommand(
    string Code, string Name, string? Description, string? Color, RoleStatus Status,
    IReadOnlyList<string> PermissionKeys) : ICommand<RoleListItem>;

public sealed class UpdateHandler : ICommandHandler<UpdateCommand, RoleListItem>
{
    private readonly IRoleRepository _roles;
    private readonly IUserUnitOfWork _unitOfWork;

    public UpdateHandler(IRoleRepository roles, IUserUnitOfWork unitOfWork)
    {
        _roles = roles;
        _unitOfWork = unitOfWork;
    }

    public ValueTask<RoleListItem> Handle(UpdateCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var role = await _roles.GetByCodeAsync(command.Code, ct)
                ?? throw new NotFoundException($"Role '{command.Code}' was not found.");

            // Recovery-anchor guard (REQ-16.5): a clean 409 before the domain backstop would throw.
            if (role.IsMerchantOwnerSeed && command.Status == RoleStatus.Inactive)
                throw new ConflictException("The merchant_owner role cannot be deactivated.");

            var catalog = await _roles.ListCatalogKeysAsync(ct);
            role.Rename(command.Name);
            role.SetDescription(command.Description);
            role.SetColor(command.Color);
            role.SetPermissions(command.PermissionKeys, catalog);
            if (command.Status == RoleStatus.Active)
                role.Activate();
            else
                role.Deactivate();

            await _unitOfWork.SaveChangesAsync(ct);

            var userCount = await _roles.CountAssignmentsForRoleAsync(role.Id, ct);
            return new RoleListItem(role.Code, role.Name, role.Description, role.Color, role.Status,
                [.. role.PermissionKeys], userCount);
        }, cancellationToken));
}

/// <summary>Deletes a merchant-user role (REQ-16). The <c>merchant_owner</c> anchor is undeletable (409, REQ-16.5); a role
/// with ≥1 bound assignment is undeletable (409); unknown role -> 404. Gated <c>merchant_user.roles.manage</c> at the host.</summary>
public sealed record DeleteCommand(string Code) : ICommand<DeleteResult>;

public sealed record DeleteResult(string Code);

public sealed class DeleteHandler : ICommandHandler<DeleteCommand, DeleteResult>
{
    private readonly IRoleRepository _roles;
    private readonly IUserUnitOfWork _unitOfWork;

    public DeleteHandler(IRoleRepository roles, IUserUnitOfWork unitOfWork)
    {
        _roles = roles;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<DeleteResult> Handle(DeleteCommand command, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var role = await _roles.GetByCodeAsync(command.Code, ct)
                ?? throw new NotFoundException($"Role '{command.Code}' was not found.");

            if (role.IsMerchantOwnerSeed)
                throw new ConflictException("The merchant_owner role cannot be deleted.");
            if (await _roles.CountAssignmentsForRoleAsync(role.Id, ct) > 0)
                throw new ConflictException("A role with bound users cannot be deleted.");

            _roles.Remove(role);
            await _unitOfWork.SaveChangesAsync(ct);
            return true; // payload unused — the result is built from command.Code below
        }, cancellationToken);

        return new DeleteResult(command.Code);
    }
}
