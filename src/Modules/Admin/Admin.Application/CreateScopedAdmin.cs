using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.CreateScopedAdmin;

/// <summary>A Super invites a Scoped admin by verified email (REQ-3.4): an <see cref="AdminAccount"/>
/// (Tier=Scoped, unbound subject) is created and a <c>create-scoped</c> audit written. The subject is bound on
/// the invitee's first login. A duplicate email is rejected (unique index -> <see cref="ConflictException"/>
/// 409). Super-only authorization is enforced at the host (RequireAdminTier).</summary>
public sealed record CreateScopedAdminCommand(string Email, Guid ActingAdminId, string CorrelationId)
    : ICommand<CreateScopedAdminResult>;

public sealed record CreateScopedAdminResult(Guid AdminId, string Email);

public sealed class CreateScopedAdminHandler : ICommandHandler<CreateScopedAdminCommand, CreateScopedAdminResult>
{
    private readonly IAdminAccountRepository _admins;
    private readonly IAdminAccountAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateScopedAdminHandler(
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

    public async ValueTask<CreateScopedAdminResult> Handle(CreateScopedAdminCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Pre-check for a clean 409 message (the unique Email index is the race-safe backstop).
            if (await _admins.GetByEmailAsync(command.Email.Trim(), ct) is not null)
                throw new ConflictException($"An admin with email '{command.Email}' already exists.");

            var account = AdminAccount.CreateScoped(command.Email, _clock.UtcNow);
            _admins.Add(account);
            _audit.Append(AdminAccountAudit.For(
                AdminAuditAction.CreateScoped, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: account.Id));
            await _unitOfWork.SaveChangesAsync(ct);
            return new CreateScopedAdminResult(account.Id, account.Email);
        }, cancellationToken);
    }
}
