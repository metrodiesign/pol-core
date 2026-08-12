using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>A Super promotes/demotes another admin between <see cref="Tier.Scoped"/> and
/// <see cref="Tier.Super"/> (rls-to-query-filter REQ-4.11 invalidation-matrix source "Tier"). The domain
/// blocks changing one's OWN tier (mirrors REQ-8.2's self-suspend guard — a lone Super demoting itself
/// could strand oversight) as a defense-in-depth invariant; the host also rejects it up front with a 403,
/// same pattern as <see cref="SuspendCommand"/>. Idempotent: setting the current tier is a no-op. An
/// unknown target -> 404. Super-only at the host.</summary>
public sealed record ChangeAdminTierCommand(
    Guid TargetAdminId, Tier NewTier, Guid ActingAdminId, string CorrelationId, long ExpectedVersion)
    : ICommand<ChangeAdminTierResult>;

public sealed record ChangeAdminTierResult(Guid AdminId, string Tier, long Version);

public sealed class ChangeAdminTierHandler : ICommandHandler<ChangeAdminTierCommand, ChangeAdminTierResult>
{
    private readonly IUserRepository _admins;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ChangeAdminTierHandler(
        IUserRepository admins,
        IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ChangeAdminTierResult> Handle(ChangeAdminTierCommand command, CancellationToken cancellationToken)
    {
        var state = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var admin = await _admins.GetByIdAsync(command.TargetAdminId, ct)
                ?? throw new NotFoundException("The admin account was not found.");
            if (admin.Version != command.ExpectedVersion)
                throw new ConflictException("The admin account changed after it was loaded.", "state_conflict");

            admin.ChangeTier(command.NewTier, command.ActingAdminId); // throws on self-change
            _audit.Append(Audit.For(
                AuditAction.TierChanged, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.TargetAdminId));
            await _unitOfWork.SaveChangesAsync(ct);
            return (admin.Tier, admin.Version);
        }, cancellationToken);

        return new ChangeAdminTierResult(command.TargetAdminId, state.Tier.ToString(), state.Version);
    }
}
