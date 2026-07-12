using Admins.Application.ResolveAdmin;
using Admins.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.SelfProvisionSuperAdmin;

/// <summary>
/// Bootstrap path (REQ-5): an allowlisted Google subject with no <see cref="PlatformUser"/> self-provisions as
/// Super/Active on first login. Idempotent — a concurrent first-login race surfaces a unique-violation
/// (translated to <see cref="ConflictException"/> by the admin unit of work) which is caught and re-read so
/// exactly one row wins and both requests resolve (REQ-5.2). The allowlist gate itself is enforced by the
/// host BEFORE this command is sent.
/// </summary>
public sealed record SelfProvisionSuperAdminCommand(string Subject, string Email, string CorrelationId)
    : ICommand<AdminResolution>;

public sealed class SelfProvisionSuperAdminHandler : ICommandHandler<SelfProvisionSuperAdminCommand, AdminResolution>
{
    private readonly IPlatformUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IPlatformUserAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SelfProvisionSuperAdminHandler(
        IPlatformUserRepository admins,
        IAdminRoleRepository roles,
        IPlatformUserAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<AdminResolution> Handle(SelfProvisionSuperAdminCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var account = PlatformUser.SelfProvision(command.Subject, command.Email, _clock.UtcNow);
                _admins.Add(account);
                _audit.Append(PlatformUserAudit.For(
                    AdminAuditAction.SelfProvision, account.Id, command.CorrelationId, _clock.UtcNow, targetAdminId: account.Id));
                // Bootstrap is usable immediately only if it also holds the super_admin role (orthogonal model has
                // no Super-bypass — REQ-8.1). Assigned in the same transaction so the account never exists roleless.
                await AssignSuperAdminRoleAsync(account.Id, command.CorrelationId, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                return new AdminResolution(account.Id, account.Email, PlatformUserTier.Super, AccessibleMerchants.All);
            }, cancellationToken);
        }
        catch (ConflictException)
        {
            // Concurrent first-login race (REQ-5.2): the other request inserted the row first. Re-read it so
            // both requests resolve the single winning account.
            var existing = await _admins.GetBySubjectAsync(command.Subject, cancellationToken)
                ?? throw new ConflictException("Self-provision raced but no admin account was found on re-read.");
            var accessible = await ResolveAdminHandler.ResolveAccessibleAsync(existing, _admins, cancellationToken);
            var permissions = await _roles.ListEffectivePermissionsAsync(existing.Id, cancellationToken);
            return new AdminResolution(existing.Id, existing.Email, existing.Tier, accessible) { Permissions = permissions };
        }
    }

    /// <summary>Idempotently binds the seed super_admin role to the bootstrap account (REQ-8.1). No-op if the seed
    /// role is absent (pre-migration) or already assigned (race/retry safe — S1).</summary>
    private async Task AssignSuperAdminRoleAsync(Guid adminId, string correlationId, CancellationToken ct)
    {
        var role = await _roles.GetByCodeAsync(AdminRole.SuperAdminCode, ct);
        if (role is null || await _roles.AssignmentExistsAsync(adminId, role.Id, ct))
            return;
        _roles.AddAssignment(AdminRoleAssignment.Create(adminId, role.Id, adminId, _clock.UtcNow));
        _audit.Append(PlatformUserAudit.For(
            AdminAuditAction.RoleAssigned, adminId, correlationId, _clock.UtcNow,
            targetAdminId: adminId, targetRoleId: role.Id));
    }
}
