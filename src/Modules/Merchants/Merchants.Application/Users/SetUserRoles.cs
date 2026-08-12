using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

using Merchants.Application.Users.Roles;

namespace Merchants.Application.Users;

/// <summary>Sets a merchant-user's roles to exactly the given set within the ACTING user's merchant (REQ-16.3): adds the
/// missing assignments and removes the extras, idempotently. The target must be an Active merchant user in the SAME merchant
/// as the actor (else 404 — no cross-merchant existence leak). Role codes are resolved to ids; an unknown code -> 400.
/// New assignments are stamped with the acting merchant + the acting merchant user as the assigner. Gated
/// <c>users.roles</c> at the host (S8).</summary>
// ponytail: DUPLICATE-shaped of Admins.Application.SetAdminRoles (+ merchant scoping and management audit) — deliberate.
public sealed record SetRolesCommand(
    Guid TargetMerchantUserId, IReadOnlyList<string> RoleCodes, Guid ActingMerchantId, Guid ActingMerchantUserId,
    string? CorrelationId = null, long? ExpectedVersion = null)
    : ICommand<SetRolesResult>;

public sealed record SetRolesResult(Guid UserId, IReadOnlyList<string> RoleCodes, long Version);

public sealed class SetRolesHandler : ICommandHandler<SetRolesCommand, SetRolesResult>
{
    private readonly IUserRepository _accounts;
    private readonly IRoleRepository _roles;
    private readonly IUserUnitOfWork _unitOfWork;
    private readonly IActiveManagerGuard _managerGuard;
    private readonly IManagementAuditWriter _audits;
    private readonly IClock _clock;

    public SetRolesHandler(
        IUserRepository accounts, IRoleRepository roles, IUserUnitOfWork unitOfWork,
        IActiveManagerGuard managerGuard, IManagementAuditWriter audits, IClock clock)
    {
        _accounts = accounts;
        _roles = roles;
        _unitOfWork = unitOfWork;
        _managerGuard = managerGuard;
        _audits = audits;
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
            var target = await _accounts.FindByIdAsync(command.TargetMerchantUserId, ct);
            // Same-merchant Active target only; anything else is invisible to the acting user (REQ-16.3 / no leak).
            if (target is null || target.Status != UserStatus.Active || target.MerchantId != command.ActingMerchantId)
                throw new NotFoundException("The merchant user was not found in your merchant.");
            if (command.ExpectedVersion is { } expectedVersion)
                target.EnsureVersion(expectedVersion);

            var resolved = await _roles.GetRoleIdsByCodesAsync(command.ActingMerchantId, requestedCodes, ct);
            var unknown = requestedCodes.Where(c => !resolved.ContainsKey(c)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown role codes: {string.Join(", ", unknown)}");

            var desired = resolved.Values.ToHashSet();
            var current = await _roles.ListRoleIdsForUserAsync(command.TargetMerchantUserId, ct);

            var manager = await _roles.GetActiveRoleIdsByCodesAsync(
                command.ActingMerchantId, ["merchant_manager"], ct);
            if (manager.TryGetValue("merchant_manager", out var managerRoleId)
                && current.Contains(managerRoleId) && !desired.Contains(managerRoleId)
                && await _managerGuard.CountActiveUsersWithRoleAsync(
                    command.ActingMerchantId, managerRoleId, ct) <= 1)
                throw new ConflictException("The last active merchant manager cannot be downgraded.");

            foreach (var roleId in desired.Where(id => !current.Contains(id)))
                _roles.AddAssignment(RoleAssignment.Create(
                    command.TargetMerchantUserId, roleId, command.ActingMerchantId, command.ActingMerchantUserId, _clock.UtcNow));

            foreach (var roleId in current.Where(id => !desired.Contains(id)))
            {
                var assignment = await _roles.GetAssignmentAsync(command.TargetMerchantUserId, roleId, ct);
                if (assignment is not null)
                    _roles.RemoveAssignment(assignment);
            }

            target.BumpVersion();

            _audits.Append(MerchantUserManagementAudit.For(
                command.ActingMerchantId, command.ActingMerchantUserId, command.TargetMerchantUserId, null,
                MerchantUserManagementAudit.Actions.SetRoles,
                string.IsNullOrWhiteSpace(command.CorrelationId) ? CorrelationId.Current : command.CorrelationId,
                _clock.UtcNow));

            await _unitOfWork.SaveChangesAsync(ct);
            return target.Version;
        }, cancellationToken);

        return new SetRolesResult(command.TargetMerchantUserId, requestedCodes, version);
    }
}
