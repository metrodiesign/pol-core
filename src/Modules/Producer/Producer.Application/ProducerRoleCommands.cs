using BuildingBlocks.Application;
using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>Creates a producer role (REQ-16). A duplicate <see cref="Code"/> -> 409 (pre-check; the unique index is
/// the race-safe backstop). A permission key outside the catalog is rejected by the aggregate (ArgumentException ->
/// 400, REQ-16.2). Gated <c>producer.roles.manage</c> at the host (S8).</summary>
// ponytail: DUPLICATE-shaped of Admin.Application.CreateRole (no audit — producer role CRUD is not in REQ-21) — deliberate.
public sealed record CreateProducerRoleCommand(
    string Code, string Name, string? Description, string? Color, ProducerRoleStatus Status,
    IReadOnlyList<string> PermissionKeys) : ICommand<ProducerRoleListItem>;

public sealed class CreateProducerRoleHandler : ICommandHandler<CreateProducerRoleCommand, ProducerRoleListItem>
{
    private readonly IProducerRoleRepository _roles;
    private readonly IProducerUnitOfWork _unitOfWork;

    public CreateProducerRoleHandler(IProducerRoleRepository roles, IProducerUnitOfWork unitOfWork)
    {
        _roles = roles;
        _unitOfWork = unitOfWork;
    }

    public ValueTask<ProducerRoleListItem> Handle(CreateProducerRoleCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var code = command.Code?.Trim() ?? string.Empty;
            if (await _roles.CodeExistsAsync(code, ct))
                throw new ConflictException($"A role with code '{code}' already exists.");

            var catalog = await _roles.ListCatalogKeysAsync(ct);
            var role = ProducerRole.Create(code, command.Name, command.Description, command.Color,
                command.Status, command.PermissionKeys, catalog);
            _roles.Add(role);
            await _unitOfWork.SaveChangesAsync(ct);

            return new ProducerRoleListItem(role.Code, role.Name, role.Description, role.Color, role.Status,
                [.. role.PermissionKeys], UserCount: 0);
        }, cancellationToken));
}

/// <summary>Updates a producer role's name/description/color/status/permissions (REQ-16.1). <c>Code</c> is immutable.
/// Unknown target -> 404; a key outside the catalog -> 400; deactivating the <c>tenant_owner</c> anchor -> 409
/// (REQ-16.5). Gated <c>producer.roles.manage</c> at the host.</summary>
public sealed record UpdateProducerRoleCommand(
    string Code, string Name, string? Description, string? Color, ProducerRoleStatus Status,
    IReadOnlyList<string> PermissionKeys) : ICommand<ProducerRoleListItem>;

public sealed class UpdateProducerRoleHandler : ICommandHandler<UpdateProducerRoleCommand, ProducerRoleListItem>
{
    private readonly IProducerRoleRepository _roles;
    private readonly IProducerUnitOfWork _unitOfWork;

    public UpdateProducerRoleHandler(IProducerRoleRepository roles, IProducerUnitOfWork unitOfWork)
    {
        _roles = roles;
        _unitOfWork = unitOfWork;
    }

    public ValueTask<ProducerRoleListItem> Handle(UpdateProducerRoleCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var role = await _roles.GetByCodeAsync(command.Code, ct)
                ?? throw new NotFoundException($"Role '{command.Code}' was not found.");

            // Recovery-anchor guard (REQ-16.5): a clean 409 before the domain backstop would throw.
            if (role.IsTenantOwnerSeed && command.Status == ProducerRoleStatus.Inactive)
                throw new ConflictException("The tenant_owner role cannot be deactivated.");

            var catalog = await _roles.ListCatalogKeysAsync(ct);
            role.Rename(command.Name);
            role.SetDescription(command.Description);
            role.SetColor(command.Color);
            role.SetPermissions(command.PermissionKeys, catalog);
            if (command.Status == ProducerRoleStatus.Active)
                role.Activate();
            else
                role.Deactivate();

            await _unitOfWork.SaveChangesAsync(ct);

            var userCount = await _roles.CountAssignmentsForRoleAsync(role.Id, ct);
            return new ProducerRoleListItem(role.Code, role.Name, role.Description, role.Color, role.Status,
                [.. role.PermissionKeys], userCount);
        }, cancellationToken));
}

/// <summary>Deletes a producer role (REQ-16). The <c>tenant_owner</c> anchor is undeletable (409, REQ-16.5); a role
/// with ≥1 bound assignment is undeletable (409); unknown role -> 404. Gated <c>producer.roles.manage</c> at the host.</summary>
public sealed record DeleteProducerRoleCommand(string Code) : ICommand<DeleteProducerRoleResult>;

public sealed record DeleteProducerRoleResult(string Code);

public sealed class DeleteProducerRoleHandler : ICommandHandler<DeleteProducerRoleCommand, DeleteProducerRoleResult>
{
    private readonly IProducerRoleRepository _roles;
    private readonly IProducerUnitOfWork _unitOfWork;

    public DeleteProducerRoleHandler(IProducerRoleRepository roles, IProducerUnitOfWork unitOfWork)
    {
        _roles = roles;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<DeleteProducerRoleResult> Handle(DeleteProducerRoleCommand command, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var role = await _roles.GetByCodeAsync(command.Code, ct)
                ?? throw new NotFoundException($"Role '{command.Code}' was not found.");

            if (role.IsTenantOwnerSeed)
                throw new ConflictException("The tenant_owner role cannot be deleted.");
            if (await _roles.CountAssignmentsForRoleAsync(role.Id, ct) > 0)
                throw new ConflictException("A role with bound users cannot be deleted.");

            _roles.Remove(role);
            await _unitOfWork.SaveChangesAsync(ct);
            return true; // payload unused — the result is built from command.Code below
        }, cancellationToken);

        return new DeleteProducerRoleResult(command.Code);
    }
}
