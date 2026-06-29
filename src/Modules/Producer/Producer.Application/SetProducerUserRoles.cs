using BuildingBlocks.Application;
using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>Sets a producer's roles to exactly the given set within the ACTING producer's tenant (REQ-16.3): adds the
/// missing assignments and removes the extras, idempotently. The target must be an Active producer in the SAME tenant
/// as the actor (else 404 — no cross-tenant existence leak). Role codes are resolved to ids; an unknown code -> 400.
/// New assignments are stamped with the acting tenant + the acting producer as the assigner. Gated
/// <c>producer.user.roles</c> at the host (S8).</summary>
// ponytail: DUPLICATE-shaped of Admin.Application.SetAdminRoles (+ tenant scoping; no audit — not in REQ-21) — deliberate.
public sealed record SetProducerUserRolesCommand(
    Guid TargetTenantUserId, IReadOnlyList<string> RoleCodes, Guid ActingTenantId, Guid ActingTenantUserId)
    : ICommand<SetProducerUserRolesResult>;

public sealed record SetProducerUserRolesResult(Guid TenantUserId, IReadOnlyList<string> RoleCodes);

public sealed class SetProducerUserRolesHandler : ICommandHandler<SetProducerUserRolesCommand, SetProducerUserRolesResult>
{
    private readonly IProducerAccountRepository _accounts;
    private readonly IProducerTenantAssignmentRepository _assignments;
    private readonly IProducerRoleRepository _roles;
    private readonly IProducerUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SetProducerUserRolesHandler(
        IProducerAccountRepository accounts, IProducerTenantAssignmentRepository assignments,
        IProducerRoleRepository roles, IProducerUnitOfWork unitOfWork, IClock clock)
    {
        _accounts = accounts;
        _assignments = assignments;
        _roles = roles;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<SetProducerUserRolesResult> Handle(SetProducerUserRolesCommand command, CancellationToken cancellationToken)
    {
        var requestedCodes = command.RoleCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var target = await _accounts.FindByIdAsync(command.TargetTenantUserId, ct);
            // Same-tenant Active target only; anything else is invisible to the acting producer (REQ-16.3 / no leak).
            if (target is null || target.Status != ProducerAccountStatus.Active)
                throw new NotFoundException("The producer was not found in your tenant.");
            var targetAssignment = await _assignments.FindByAccountIdAsync(target.Id, ct);
            if (targetAssignment is null || targetAssignment.TenantId != command.ActingTenantId)
                throw new NotFoundException("The producer was not found in your tenant.");

            var resolved = await _roles.GetRoleIdsByCodesAsync(requestedCodes, ct);
            var unknown = requestedCodes.Where(c => !resolved.ContainsKey(c)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown role codes: {string.Join(", ", unknown)}");

            var desired = resolved.Values.ToHashSet();
            var current = await _roles.ListRoleIdsForUserAsync(command.TargetTenantUserId, ct);

            foreach (var roleId in desired.Where(id => !current.Contains(id)))
                _roles.AddAssignment(ProducerRoleAssignment.Create(
                    command.TargetTenantUserId, roleId, command.ActingTenantId, command.ActingTenantUserId, _clock.UtcNow));

            foreach (var roleId in current.Where(id => !desired.Contains(id)))
            {
                var assignment = await _roles.GetAssignmentAsync(command.TargetTenantUserId, roleId, ct);
                if (assignment is not null)
                    _roles.RemoveAssignment(assignment);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        return new SetProducerUserRolesResult(command.TargetTenantUserId, requestedCodes);
    }
}
