using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.UpdateAdminProfile;

/// <summary>Edits an admin's org-profile FKs (Position/Office/Level/Division). A full replace — a null field
/// clears that dimension. Each non-null FK must reference an existing, ACTIVE master (ArgumentException -> 400).
/// Unknown target admin -> 404. The load, validation, update, and audit run in one keyed "admin" transaction.
/// Gated <c>user.manage</c> at the host.</summary>
public sealed record UpdateAdminProfileCommand(
    Guid TargetAdminId, Guid? PositionId, Guid? OfficeId, Guid? LevelId, Guid? DivisionId,
    Guid ActingAdminId, string CorrelationId) : ICommand<Unit>;

public sealed class UpdateAdminProfileHandler : ICommandHandler<UpdateAdminProfileCommand, Unit>
{
    private readonly IAdminAccountRepository _admins;
    private readonly IMasterDataStore _masters;
    private readonly IAdminAccountAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public UpdateAdminProfileHandler(
        IAdminAccountRepository admins,
        IMasterDataStore masters,
        IAdminAccountAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _masters = masters;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<Unit> Handle(UpdateAdminProfileCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Load INSIDE the lambda so an execution-strategy retry re-loads a fresh entity (mirrors ReactivateAdmin).
            var admin = await _admins.GetByIdAsync(command.TargetAdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");

            await _masters.ValidateProfileFksAsync(
                command.PositionId, command.OfficeId, command.LevelId, command.DivisionId, ct);

            admin.UpdateProfile(command.PositionId, command.OfficeId, command.LevelId, command.DivisionId);
            _audit.Append(AdminAccountAudit.For(
                AdminAuditAction.UpdateProfile, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.TargetAdminId));
            await _unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }, cancellationToken);
    }
}
