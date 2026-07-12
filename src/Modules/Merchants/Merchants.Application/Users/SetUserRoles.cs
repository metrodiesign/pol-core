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
// ponytail: DUPLICATE-shaped of Admins.Application.SetAdminRoles (+ merchant scoping; no audit — not in REQ-21) — deliberate.
public sealed record SetRolesCommand(
    Guid TargetMerchantUserId, IReadOnlyList<string> RoleCodes, Guid ActingMerchantId, Guid ActingMerchantUserId)
    : ICommand<SetRolesResult>;

public sealed record SetRolesResult(Guid MerchantUserId, IReadOnlyList<string> RoleCodes);

public sealed class SetRolesHandler : ICommandHandler<SetRolesCommand, SetRolesResult>
{
    private readonly IUserRepository _accounts;
    private readonly IRoleRepository _roles;
    private readonly IUserUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SetRolesHandler(
        IUserRepository accounts, IRoleRepository roles, IUserUnitOfWork unitOfWork, IClock clock)
    {
        _accounts = accounts;
        _roles = roles;
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

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var target = await _accounts.FindByIdAsync(command.TargetMerchantUserId, ct);
            // Same-merchant Active target only; anything else is invisible to the acting user (REQ-16.3 / no leak).
            if (target is null || target.Status != UserStatus.Active || target.MerchantId != command.ActingMerchantId)
                throw new NotFoundException("The merchant user was not found in your merchant.");

            var resolved = await _roles.GetRoleIdsByCodesAsync(command.ActingMerchantId, requestedCodes, ct);
            var unknown = requestedCodes.Where(c => !resolved.ContainsKey(c)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown role codes: {string.Join(", ", unknown)}");

            var desired = resolved.Values.ToHashSet();
            var current = await _roles.ListRoleIdsForUserAsync(command.TargetMerchantUserId, ct);

            foreach (var roleId in desired.Where(id => !current.Contains(id)))
                _roles.AddAssignment(RoleAssignment.Create(
                    command.TargetMerchantUserId, roleId, command.ActingMerchantId, command.ActingMerchantUserId, _clock.UtcNow));

            foreach (var roleId in current.Where(id => !desired.Contains(id)))
            {
                var assignment = await _roles.GetAssignmentAsync(command.TargetMerchantUserId, roleId, ct);
                if (assignment is not null)
                    _roles.RemoveAssignment(assignment);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        return new SetRolesResult(command.TargetMerchantUserId, requestedCodes);
    }
}
