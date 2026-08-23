using Admins.Domain.Users;
using Admins.Application.Roles;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel;

namespace Admins.Application.Users;

/// <summary>JIT-provisions an eligible Microsoft workforce identity as an Active, Scoped, roleless admin.
/// Eligibility is enforced by the host; this command owns the atomic identity mutation and state resolution.</summary>
public sealed record JitProvisionMicrosoftAdminCommand(
    ProviderIdentity Identity, string Email, string CorrelationId) : ICommand<ResolveResult>;

public sealed class JitProvisionMicrosoftAdminHandler :
    ICommandHandler<JitProvisionMicrosoftAdminCommand, ResolveResult>
{
    private readonly IUserRepository _admins;
    private readonly IRoleRepository _roles;
    private readonly IAuditWriter _audit;
    private readonly IAdminIdentityRecoveryReader _recovery;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public JitProvisionMicrosoftAdminHandler(
        IUserRepository admins,
        IRoleRepository roles,
        IAuditWriter audit,
        IAdminIdentityRecoveryReader recovery,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _audit = audit;
        _recovery = recovery;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ResolveResult> Handle(
        JitProvisionMicrosoftAdminCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(command.Identity.Provider, User.MicrosoftProvider, StringComparison.Ordinal))
            throw new ArgumentException("JIT provisioning requires a Microsoft identity.", nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CorrelationId);
        if (!Guid.TryParse(command.Identity.Subject, out var objectId) || objectId == Guid.Empty)
            throw new ArgumentException("A valid Microsoft object id is required.", nameof(command));

        var identity = new ProviderIdentity(User.MicrosoftProvider, objectId.ToString("D").ToLowerInvariant());

        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await _admins.AcquireIdentityMutationLockAsync(ct);

                var existing = await _admins.GetByIdentityAsync(identity, ct);
                if (existing is not null)
                    return await ResolveExistingAsync(existing, ct);

                if (await _admins.GetByEmailAsync(command.Email, ct) is not null)
                    return ResolveResult.IdentityConflict;

                var now = _clock.UtcNow;
                var account = User.JitProvisionMicrosoft(command.Identity.Subject, command.Email, now);
                _admins.Add(account);
                _audit.Append(Audit.For(
                    AuditAction.JitProvision, account.Id, command.CorrelationId, now,
                    targetAdminId: account.Id));
                await _unitOfWork.SaveChangesAsync(ct);
                return await ResolveExistingAsync(account, ct);
            }, cancellationToken);
        }
        catch (ConflictException)
        {
            // The failed transaction's context is cleared/disposed by its unit of work. Never read through it.
            var recovered = await _recovery.ResolveAfterConflictAsync(identity, cancellationToken);
            return recovered.Outcome == ResolveOutcome.NotFound
                ? ResolveResult.IdentityConflict
                : recovered;
        }
    }

    private async Task<ResolveResult> ResolveExistingAsync(User account, CancellationToken cancellationToken)
    {
        if (account.Status == UserStatus.Suspended)
            return ResolveResult.Suspended;

        var accessible = await ResolveHandler.ResolveAccessibleAsync(account, _admins, cancellationToken);
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, cancellationToken);
        return ResolveResult.Of(new Resolution(account.Id, account.Email, account.Tier, accessible)
        {
            Permissions = permissions,
            AuthorizationVersion = account.AuthorizationVersion
        });
    }
}
