using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>A Super invites a Scoped admin by verified email (REQ-3.4): an <see cref="User"/>
/// (Tier=Scoped, unbound subject) is created and a <c>create-scoped</c> audit written. The subject is bound on
/// the invitee's first login. A duplicate email is rejected (unique index -> <see cref="ConflictException"/>
/// 409). Super-only authorization is enforced at the host (RequirePlatformUserTier).</summary>
public sealed record CreateScopedCommand(
    string Email, Guid ActingAdminId, string CorrelationId,
    Guid? PositionId = null, Guid? OfficeId = null, Guid? LevelId = null, Guid? DivisionId = null)
    : ICommand<CreateScopedResult>;

public sealed record CreateScopedResult(Guid AdminId, string Email);

public sealed class CreateScopedHandler : ICommandHandler<CreateScopedCommand, CreateScopedResult>
{
    private readonly IUserRepository _admins;
    private readonly IAuditWriter _audit;
    private readonly IMasterDataLookup _masters;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateScopedHandler(
        IUserRepository admins,
        IAuditWriter audit,
        IMasterDataLookup masters,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _audit = audit;
        _masters = masters;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<CreateScopedResult> Handle(CreateScopedCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Pre-check for a clean 409 message (the unique Email index is the race-safe backstop).
            if (await _admins.GetByEmailAsync(command.Email.Trim(), ct) is not null)
                throw new ConflictException($"An admin with email '{command.Email}' already exists.");

            // Any supplied org-profile FK must reference an existing, active master (-> 400 otherwise).
            await _masters.ValidateProfileFksAsync(
                command.PositionId, command.OfficeId, command.LevelId, command.DivisionId, ct);

            var account = User.CreateScoped(
                command.Email, _clock.UtcNow,
                command.PositionId, command.OfficeId, command.LevelId, command.DivisionId);
            _admins.Add(account);
            _audit.Append(Audit.For(
                AuditAction.CreateScoped, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: account.Id));
            await _unitOfWork.SaveChangesAsync(ct);
            return new CreateScopedResult(account.Id, account.Email);
        }, cancellationToken);
    }
}
