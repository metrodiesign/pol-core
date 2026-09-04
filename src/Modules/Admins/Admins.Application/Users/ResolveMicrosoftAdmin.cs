using Admins.Application.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>Resolves one validated Microsoft workforce tuple. Email is optional contact data and is never queried.</summary>
public sealed record ResolveMicrosoftAdminCommand(
    Guid TenantId,
    Guid ObjectId,
    string? Email,
    string? EmployeeId,
    string CorrelationId) : ICommand<ResolveResult>;

/// <summary>Thrown inside the keyed Admin transaction so identity, profile and audit writes roll back together.</summary>
public sealed class EmployeeProfileDeniedException(ResolveResult result) : Exception("Employee profile resolution was denied.")
{
    public ResolveResult Result { get; } = result;
}

public sealed class ResolveMicrosoftAdminHandler :
    ICommandHandler<ResolveMicrosoftAdminCommand, ResolveResult>
{
    private readonly IUserRepository _admins;
    private readonly IRoleRepository _roles;
    private readonly IAuditWriter _audit;
    private readonly IAdminIdentityRecoveryReader _recovery;
    private readonly IEmployeeProfileReader _profiles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ResolveMicrosoftAdminHandler(
        IUserRepository admins,
        IRoleRepository roles,
        IAuditWriter audit,
        IAdminIdentityRecoveryReader recovery,
        IEmployeeProfileReader profiles,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _roles = roles;
        _audit = audit;
        _recovery = recovery;
        _profiles = profiles;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ResolveResult> Handle(
        ResolveMicrosoftAdminCommand command, CancellationToken cancellationToken)
    {
        if (command.TenantId == Guid.Empty)
            throw new ArgumentException("A non-empty workforce tenant ID is required.", nameof(command));
        if (command.ObjectId == Guid.Empty)
            throw new ArgumentException("A non-empty Microsoft object ID is required.", nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CorrelationId);

        var email = AdminContactEmail.TryNormalize(command.Email, out var normalizedEmail)
            ? normalizedEmail
            : null;
        var normalized = command with { Email = email };

        try
        {
            return await RunAsync(normalized, cancellationToken);
        }
        catch (EmployeeProfileDeniedException denied)
        {
            return denied.Result;
        }
        catch (ConflictException) when (command.EmployeeId is null)
        {
            return await _recovery.ResolveAfterConflictAsync(
                command.TenantId, command.ObjectId, cancellationToken);
        }
        catch (ConflictException)
        {
            try
            {
                return await RunAsync(normalized, cancellationToken);
            }
            catch (EmployeeProfileDeniedException denied)
            {
                return denied.Result;
            }
            catch (ConflictException)
            {
                return ResolveResult.EmployeeConflict(ResolveResult.EmployeeTakenReason);
            }
        }
    }

    private Task<ResolveResult> RunAsync(
        ResolveMicrosoftAdminCommand command, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _admins.AcquireIdentityMutationLockAsync(ct);
            var account = await _admins.GetByMicrosoftIdentityAsync(command.TenantId, command.ObjectId, ct);
            var wasExisting = account is not null;
            var now = _clock.UtcNow;
            if (account is null)
            {
                account = User.JitProvisionMicrosoft(command.TenantId, command.ObjectId, command.Email, now);
                _admins.Add(account);
                _audit.Append(Audit.For(
                    AuditAction.JitProvision, account.Id, command.CorrelationId, now, targetAdminId: account.Id));
            }
            else if (account.Status == UserStatus.Suspended)
            {
                return ResolveResult.Suspended;
            }

            if (command.EmployeeId is not null)
                await ApplyEmployeeProfileAsync(
                    account, wasExisting, command.EmployeeId, command.CorrelationId, now, ct);

            await _unitOfWork.SaveChangesAsync(ct);
            return await ResolveAsync(account, ct);
        }, cancellationToken);

    private async Task ApplyEmployeeProfileAsync(
        User account,
        bool wasExisting,
        string employeeId,
        string correlationId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (account.EmployeeId is not null && !string.Equals(account.EmployeeId, employeeId, StringComparison.Ordinal))
            throw new EmployeeProfileDeniedException(ResolveResult.EmployeeConflict(ResolveResult.EmployeeMismatchReason));

        if (await _admins.GetByEmployeeIdAsync(employeeId, account.Id, cancellationToken) is not null)
            throw new EmployeeProfileDeniedException(ResolveResult.EmployeeConflict(ResolveResult.EmployeeTakenReason));

        var lookup = await _profiles.LookupAsync(employeeId, cancellationToken);
        if (lookup.Status != EmployeeProfileStatus.Found)
            throw new EmployeeProfileDeniedException(lookup.Status switch
            {
                EmployeeProfileStatus.Missing => ResolveResult.EmployeeProfileMissing,
                EmployeeProfileStatus.Invalid => ResolveResult.EmployeeProfileInvalid,
                EmployeeProfileStatus.SourceUnavailable => ResolveResult.HrSourceUnavailable,
                _ => throw new InvalidOperationException($"Unexpected employee profile status {lookup.Status}."),
            });

        var profile = lookup.Profile!;
        var change = account.ApplyEmployeeProfile(employeeId, profile.FirstName, profile.LastName);
        if (change.EmployeeBound)
            _audit.Append(Audit.For(AuditAction.EmployeeBind, account.Id, correlationId, now, targetAdminId: account.Id));
        if (wasExisting && change.NamesChanged)
            _audit.Append(Audit.For(
                AuditAction.EmployeeProfileSync, account.Id, correlationId, now, targetAdminId: account.Id));
    }

    private async Task<ResolveResult> ResolveAsync(User account, CancellationToken cancellationToken)
    {
        var accessible = await ResolveHandler.ResolveAccessibleAsync(account, _admins, cancellationToken);
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, cancellationToken);
        return ResolveResult.Of(new Resolution(account.Id, account.Email, account.Tier, accessible)
        {
            Permissions = permissions,
            AuthorizationVersion = account.AuthorizationVersion,
        });
    }
}
