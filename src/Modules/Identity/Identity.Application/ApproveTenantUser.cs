using BuildingBlocks.Application;
using Identity.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Application.ApproveTenantUser;

/// <summary>Admin approves a pending TenantUser onto a tenant the admin selected, with a role (REQ-5).
/// TenantId comes ONLY from the admin's validated selection; AdminSubject + CorrelationId are set by the
/// host from the authenticated request.</summary>
public sealed record ApproveTenantUserCommand(
    string Subject, Guid TenantId, TenantUserRole Role, string AdminSubject, string CorrelationId)
    : ICommand<ApproveTenantUserResult>;

public sealed record ApproveTenantUserResult(Guid TenantUserId, Guid TenantId, string Role);

public sealed class ApproveTenantUserHandler
    : ICommandHandler<ApproveTenantUserCommand, ApproveTenantUserResult>
{
    private readonly ITenantUserRepository _users;
    private readonly ITenantDirectory _tenants;
    private readonly IRegistrationAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ApproveTenantUserHandler(
        ITenantUserRepository users,
        ITenantDirectory tenants,
        IRegistrationAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _users = users;
        _tenants = tenants;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ApproveTenantUserResult> Handle(
        ApproveTenantUserCommand command, CancellationToken cancellationToken)
    {
        // The selected tenant must exist AND be active — never trust a value the applicant supplied (REQ-5.3/5.4).
        if (!await _tenants.IsActiveTenantAsync(command.TenantId, cancellationToken))
            throw new ConflictException("The selected tenant does not exist or is not active.");

        var tenantUserId = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var user = await _users.GetBySubjectAsync(command.Subject, ct)
                ?? throw new NotFoundException("The tenant user was not found.");

            user.Approve(command.TenantId, command.Role, _clock.UtcNow); // guards non-pending (REQ-5.5)
            _audit.Append(RegistrationAudit.ForApproval(
                command.AdminSubject, command.Subject, command.TenantId, command.CorrelationId, _clock.UtcNow));

            await _unitOfWork.SaveChangesAsync(ct);
            return user.Id;
        }, cancellationToken);

        return new ApproveTenantUserResult(tenantUserId, command.TenantId, command.Role.ToString());
    }
}
