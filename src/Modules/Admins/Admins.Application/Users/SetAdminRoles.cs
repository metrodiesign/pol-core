using Admins.Application.Roles;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>Sets an admin's roles to exactly the given set (REQ-4.2): adds the missing assignments and removes the
/// extra ones, idempotently. Role codes are resolved to ids; an unknown code -> 400 (S7). Unknown admin -> 404.
/// Each add/remove is audited (REQ-10). Gated <c>user.roles</c> at the host.</summary>
public sealed record SetRolesCommand(
    Guid AdminId, IReadOnlyList<string> RoleCodes, Guid ActingAdminId, string CorrelationId, long ExpectedVersion)
    : ICommand<SetRolesResult>;

public sealed record SetRolesResult(Guid AdminId, IReadOnlyList<string> RoleCodes, long Version);

public sealed class SetRolesHandler : ICommandHandler<SetRolesCommand, SetRolesResult>
{
    private readonly IRoleRepository _roles;
    private readonly IUserRepository _admins;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SetRolesHandler(
        IRoleRepository roles,
        IUserRepository admins,
        IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _roles = roles;
        _admins = admins;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<SetRolesResult> Handle(SetRolesCommand command, CancellationToken cancellationToken)
    {
        var requestedCodes = command.RoleCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var version = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var admin = await _admins.GetByIdAsync(command.AdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");
            if (admin.Version != command.ExpectedVersion)
                throw new ConflictException("The admin account changed after it was loaded.", "state_conflict");

            var resolved = await _roles.GetRoleIdsByCodesAsync(requestedCodes, ct);
            var unknown = requestedCodes.Where(c => !resolved.ContainsKey(c)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown role codes: {string.Join(", ", unknown)}");

            var desired = resolved.Values.ToHashSet();
            var current = await _roles.ListRoleIdsForAdminAsync(command.AdminId, ct);

            foreach (var roleId in desired.Where(id => !current.Contains(id)))
            {
                _roles.AddAssignment(RoleAssignment.Create(command.AdminId, roleId, command.ActingAdminId, _clock.UtcNow));
                _audit.Append(Audit.For(
                    AuditAction.RoleAssigned, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                    targetAdminId: command.AdminId, targetRoleId: roleId));
            }

            foreach (var roleId in current.Where(id => !desired.Contains(id)))
            {
                var assignment = await _roles.GetAssignmentAsync(command.AdminId, roleId, ct);
                if (assignment is null)
                    continue;
                _roles.RemoveAssignment(assignment);
                _audit.Append(Audit.For(
                    AuditAction.RoleUnassigned, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                    targetAdminId: command.AdminId, targetRoleId: roleId));
            }

            if (!desired.SetEquals(current))
            {
                admin.BumpAuthorizationVersion();
                admin.BumpResourceVersion();
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return admin.Version;
        }, cancellationToken);

        return new SetRolesResult(command.AdminId, requestedCodes, version);
    }
}
