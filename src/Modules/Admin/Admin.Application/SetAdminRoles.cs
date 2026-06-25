using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.SetAdminRoles;

/// <summary>Sets an admin's roles to exactly the given set (REQ-4.2): adds the missing assignments and removes the
/// extra ones, idempotently. Role codes are resolved to ids; an unknown code -> 400 (S7). Unknown admin -> 404.
/// Each add/remove is audited (REQ-10). Gated <c>user.roles</c> at the host.</summary>
public sealed record SetAdminRolesCommand(
    Guid AdminId, IReadOnlyList<string> RoleCodes, Guid ActingAdminId, string CorrelationId)
    : ICommand<SetAdminRolesResult>;

public sealed record SetAdminRolesResult(Guid AdminId, IReadOnlyList<string> RoleCodes);

public sealed class SetAdminRolesHandler : ICommandHandler<SetAdminRolesCommand, SetAdminRolesResult>
{
    private readonly IAdminRoleRepository _roles;
    private readonly IAdminAccountRepository _admins;
    private readonly IAdminAccountAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SetAdminRolesHandler(
        IAdminRoleRepository roles,
        IAdminAccountRepository admins,
        IAdminAccountAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _roles = roles;
        _admins = admins;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<SetAdminRolesResult> Handle(SetAdminRolesCommand command, CancellationToken cancellationToken)
    {
        var requestedCodes = command.RoleCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (await _admins.GetByIdAsync(command.AdminId, ct) is null)
                throw new NotFoundException("The admin account was not found.");

            var resolved = await _roles.GetRoleIdsByCodesAsync(requestedCodes, ct);
            var unknown = requestedCodes.Where(c => !resolved.ContainsKey(c)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown role codes: {string.Join(", ", unknown)}");

            var desired = resolved.Values.ToHashSet();
            var current = await _roles.ListRoleIdsForAdminAsync(command.AdminId, ct);

            foreach (var roleId in desired.Where(id => !current.Contains(id)))
            {
                _roles.AddAssignment(AdminRoleAssignment.Create(command.AdminId, roleId, command.ActingAdminId, _clock.UtcNow));
                _audit.Append(AdminAccountAudit.For(
                    AdminAuditAction.RoleAssigned, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                    targetAdminId: command.AdminId, targetRoleId: roleId));
            }

            foreach (var roleId in current.Where(id => !desired.Contains(id)))
            {
                var assignment = await _roles.GetAssignmentAsync(command.AdminId, roleId, ct);
                if (assignment is null)
                    continue;
                _roles.RemoveAssignment(assignment);
                _audit.Append(AdminAccountAudit.For(
                    AdminAuditAction.RoleUnassigned, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                    targetAdminId: command.AdminId, targetRoleId: roleId));
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return command.AdminId;
        }, cancellationToken);

        return new SetAdminRolesResult(command.AdminId, requestedCodes);
    }
}
