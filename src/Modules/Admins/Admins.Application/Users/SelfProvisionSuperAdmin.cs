using Admins.Application.Roles;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>
/// Bootstrap path (REQ-5): an allowlisted Google subject with no <see cref="User"/> self-provisions as
/// Super/Active on first login. Idempotent — a concurrent first-login race surfaces a unique-violation
/// (translated to <see cref="ConflictException"/> by the admin unit of work) which is caught and re-read so
/// exactly one row wins and both requests resolve (REQ-5.2). The allowlist gate itself is enforced by the
/// host BEFORE this command is sent.
/// </summary>
public sealed record SelfProvisionSuperCommand(string Subject, string Email, string CorrelationId)
    : ICommand<Resolution>;

public sealed class SelfProvisionSuperHandler : ICommandHandler<SelfProvisionSuperCommand, Resolution>
{
    private readonly IUserRepository _admins;
    private readonly IRoleRepository _roles;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SelfProvisionSuperHandler(
        IUserRepository admins,
        IRoleRepository roles,
        IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<Resolution> Handle(SelfProvisionSuperCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var account = User.SelfProvision(command.Subject, command.Email, _clock.UtcNow);
                _admins.Add(account);
                _audit.Append(Audit.For(
                    AuditAction.SelfProvision, account.Id, command.CorrelationId, _clock.UtcNow, targetAdminId: account.Id));
                // Bootstrap is usable immediately only if it also holds the super_admin role (orthogonal model has
                // no Super-bypass — REQ-8.1). Assigned in the same transaction so the account never exists roleless.
                await AssignSuperAdminRoleAsync(account.Id, command.CorrelationId, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                return new Resolution(account.Id, account.Email, Tier.Super, AccessibleMerchants.All);
            }, cancellationToken);
        }
        catch (ConflictException)
        {
            // Concurrent first-login race (REQ-5.2): the other request inserted the row first. Re-read it so
            // both requests resolve the single winning account.
            var existing = await _admins.GetBySubjectAsync(command.Subject, cancellationToken)
                ?? throw new ConflictException("Self-provision raced but no admin account was found on re-read.");
            var accessible = await ResolveHandler.ResolveAccessibleAsync(existing, _admins, cancellationToken);
            var permissions = await _roles.ListEffectivePermissionsAsync(existing.Id, cancellationToken);
            return new Resolution(existing.Id, existing.Email, existing.Tier, accessible) { Permissions = permissions };
        }
    }

    /// <summary>Idempotently binds the seed super_admin role to the bootstrap account (REQ-8.1). No-op if the seed
    /// role is absent (pre-migration) or already assigned (race/retry safe — S1).</summary>
    private async Task AssignSuperAdminRoleAsync(Guid adminId, string correlationId, CancellationToken ct)
    {
        var role = await _roles.GetByCodeAsync(Role.SuperAdminCode, ct);
        if (role is null || await _roles.AssignmentExistsAsync(adminId, role.Id, ct))
            return;
        _roles.AddAssignment(RoleAssignment.Create(adminId, role.Id, adminId, _clock.UtcNow));
        _audit.Append(Audit.For(
            AuditAction.RoleAssigned, adminId, correlationId, _clock.UtcNow,
            targetAdminId: adminId, targetRoleId: role.Id));
    }
}
