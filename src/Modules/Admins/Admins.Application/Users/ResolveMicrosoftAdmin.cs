using Admins.Application.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>Resolves one canonical Tier 0 email through existing identity, binding, or roleless JIT.</summary>
public sealed record ResolveMicrosoftAdminCommand(string CanonicalEmail, string CorrelationId)
    : ICommand<ResolveResult>;

public static class Tier0CandidatePolicy
{
    public static bool HasExactEmailOwnership(User account, string canonicalEmail) =>
        WorkforceEmail.TryCanonicalize(account.Email, out var stored)
        && string.Equals(stored, canonicalEmail, StringComparison.Ordinal)
        && string.Equals(account.WorkforceEmailKey, canonicalEmail, StringComparison.Ordinal);

    public static bool HasExactMicrosoftIdentity(User account, string canonicalEmail) =>
        string.Equals(account.Provider, User.MicrosoftProvider, StringComparison.Ordinal)
        && string.Equals(account.Subject, canonicalEmail, StringComparison.Ordinal);

    public static bool IsExactResolvedOwner(User account, string canonicalEmail) =>
        HasExactMicrosoftIdentity(account, canonicalEmail)
        && HasExactEmailOwnership(account, canonicalEmail);
}

public sealed class ResolveMicrosoftAdminHandler :
    ICommandHandler<ResolveMicrosoftAdminCommand, ResolveResult>
{
    private readonly IUserRepository _admins;
    private readonly IRoleRepository _roles;
    private readonly IAuditWriter _audit;
    private readonly IAdminIdentityRecoveryReader _recovery;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ResolveMicrosoftAdminHandler(
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
        ResolveMicrosoftAdminCommand command, CancellationToken cancellationToken)
    {
        if (!WorkforceEmail.TryCanonicalize(command.CanonicalEmail, out var canonicalEmail))
            throw new ArgumentException("A valid corporate email is required.", nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CorrelationId);

        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await _admins.AcquireIdentityMutationLockAsync(ct);
                var candidates = await _admins.ListTier0CandidatesAsync(canonicalEmail, ct);
                if (candidates.Count > 1)
                    return ResolveResult.IdentityConflict;

                if (candidates.Count == 0)
                    return await CreateAsync(canonicalEmail, command.CorrelationId, ct);

                var account = candidates[0];
                var emailOwner = Tier0CandidatePolicy.HasExactEmailOwnership(account, canonicalEmail);
                var identityOwner = Tier0CandidatePolicy.HasExactMicrosoftIdentity(account, canonicalEmail);
                if (!emailOwner)
                    return ResolveResult.IdentityConflict;

                if (account.Status == UserStatus.Suspended)
                    return ResolveResult.Suspended;

                if (identityOwner)
                    return await ResolveAsync(account, ct);
                if (account.Subject is not null)
                    return ResolveResult.IdentityConflict;

                account.BindSubject(User.MicrosoftProvider, canonicalEmail);
                var now = _clock.UtcNow;
                _audit.Append(Audit.For(
                    AuditAction.MicrosoftEmailBind, account.Id, command.CorrelationId, now,
                    targetAdminId: account.Id));
                await _unitOfWork.SaveChangesAsync(ct);
                return await ResolveAsync(account, ct);
            }, cancellationToken);
        }
        catch (ConflictException)
        {
            return await _recovery.ResolveAfterConflictAsync(canonicalEmail, cancellationToken);
        }
    }

    private async Task<ResolveResult> CreateAsync(
        string canonicalEmail, string correlationId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var account = User.JitProvisionMicrosoft(canonicalEmail, now);
        _admins.Add(account);
        _audit.Append(Audit.For(
            AuditAction.JitProvision, account.Id, correlationId, now,
            targetAdminId: account.Id));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await ResolveAsync(account, cancellationToken);
    }

    private async Task<ResolveResult> ResolveAsync(User account, CancellationToken cancellationToken)
    {
        var accessible = await ResolveHandler.ResolveAccessibleAsync(account, _admins, cancellationToken);
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, cancellationToken);
        return ResolveResult.Of(new Resolution(account.Id, account.Email, account.Tier, accessible)
        {
            Permissions = permissions,
            AuthorizationVersion = account.AuthorizationVersion
        });
    }
}
