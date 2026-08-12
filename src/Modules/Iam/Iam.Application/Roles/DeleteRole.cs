using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Iam.Application.Roles;

/// <summary>Deletes a role on either side (REQ-2/6.1/6.2). Invisible to <see cref="RoleSideContext"/> -> 404
/// (REQ-6.3). A Merchant-side caller deleting a role it does not own (incl. a visible shared seed) -> 409
/// (REQ-3.8). A seed anchor is undeletable (409, REQ-2.4). A role with ≥1 bound assignment on EITHER side is
/// undeletable (409, REQ-6.3) — cascade removes the role's own permission grants. Gated <c>user.roles</c>
/// (admin) / <c>roles.manage</c> (merchant) at the host.</summary>
public sealed record DeleteRoleCommand(
    RoleSideContext Context, string Code, string CorrelationId, long? ExpectedVersion = null)
    : ICommand<DeleteRoleResult>;

public sealed record DeleteRoleResult(string Code);

public sealed class DeleteRoleHandler : ICommandHandler<DeleteRoleCommand, DeleteRoleResult>
{
    private readonly IRoleStore _roles;
    private readonly IRoleAssignmentCounter _counter;
    private readonly IRoleAuditSink _audit;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteRoleHandler(
        IRoleStore roles, IRoleAssignmentCounter counter, IRoleAuditSink audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork)
    {
        _roles = roles;
        _counter = counter;
        _audit = audit;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<DeleteRoleResult> Handle(DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var role = await _roles.GetByCodeAsync(command.Context, command.Code, ct)
                ?? throw new NotFoundException($"Role '{command.Code}' was not found.");

            if (command.Context.Scope == Scope.Merchant && role.MerchantId != command.Context.MerchantId)
                throw new ConflictException($"Role '{command.Code}' cannot be deleted by this merchant.");
            if (command.ExpectedVersion is { } expectedVersion && role.Version != expectedVersion)
                throw new ConflictException("The role changed after it was loaded.", "state_conflict");
            if (role.IsSeedAnchor)
                throw new ConflictException($"The {role.Code} role cannot be deleted.");
            if (await _counter.CountAsync(command.Context, role.Id, ct) > 0)
                throw new ConflictException("A role with bound users cannot be deleted.");

            _roles.Remove(role);
            _audit.RoleDeleted(role.Id, command.CorrelationId);
            await _unitOfWork.SaveChangesAsync(ct);
            return true; // transaction payload is unused — the result is built from command.Code below
        }, cancellationToken);

        return new DeleteRoleResult(command.Code);
    }
}
