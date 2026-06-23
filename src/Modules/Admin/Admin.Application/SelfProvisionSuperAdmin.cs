using Admin.Application.ResolveAdmin;
using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.SelfProvisionSuperAdmin;

/// <summary>
/// Bootstrap path (REQ-5): an allowlisted Google subject with no <see cref="AdminAccount"/> self-provisions as
/// Super/Active on first login. Idempotent — a concurrent first-login race surfaces a unique-violation
/// (translated to <see cref="ConflictException"/> by the admin unit of work) which is caught and re-read so
/// exactly one row wins and both requests resolve (REQ-5.2). The allowlist gate itself is enforced by the
/// host BEFORE this command is sent.
/// </summary>
public sealed record SelfProvisionSuperAdminCommand(string Subject, string Email, string CorrelationId)
    : ICommand<AdminResolution>;

public sealed class SelfProvisionSuperAdminHandler : ICommandHandler<SelfProvisionSuperAdminCommand, AdminResolution>
{
    private readonly IAdminAccountRepository _admins;
    private readonly IAdminAccountAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SelfProvisionSuperAdminHandler(
        IAdminAccountRepository admins,
        IAdminAccountAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
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
                var account = AdminAccount.SelfProvision(command.Subject, command.Email, _clock.UtcNow);
                _admins.Add(account);
                _audit.Append(AdminAccountAudit.For(
                    AdminAuditAction.SelfProvision, account.Id, command.CorrelationId, _clock.UtcNow, targetAdminId: account.Id));
                await _unitOfWork.SaveChangesAsync(ct);
                return new AdminResolution(account.Id, account.Email, AdminTier.Super, AccessibleTenants.All);
            }, cancellationToken);
        }
        catch (ConflictException)
        {
            // Concurrent first-login race (REQ-5.2): the other request inserted the row first. Re-read it so
            // both requests resolve the single winning account.
            var existing = await _admins.GetBySubjectAsync(command.Subject, cancellationToken)
                ?? throw new ConflictException("Self-provision raced but no admin account was found on re-read.");
            var accessible = await ResolveAdminHandler.ResolveAccessibleAsync(existing, _admins, cancellationToken);
            return new AdminResolution(existing.Id, existing.Email, existing.Tier, accessible);
        }
    }
}
