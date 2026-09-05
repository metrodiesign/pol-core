using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>Creates an approved, pre-bound Microsoft Scoped Admin under the persisted tenant pin.</summary>
public sealed record CreateScopedCommand(
    Guid ObjectId,
    string? Email,
    string IdentityApprovalReference,
    Guid ActingAdminId,
    string CorrelationId) : ICommand<CreateScopedResult>;

public sealed record CreateScopedResult(Guid AdminId, string? Email);

public sealed class CreateScopedHandler : ICommandHandler<CreateScopedCommand, CreateScopedResult>
{
    public const int ApprovalReferenceMaxLength = 128;

    private readonly IUserRepository _admins;
    private readonly IAuditWriter _audit;
    private readonly IWorkforceTenantBindingStore _tenantBinding;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateScopedHandler(
        IUserRepository admins,
        IAuditWriter audit,
        IWorkforceTenantBindingStore tenantBinding,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _audit = audit;
        _tenantBinding = tenantBinding;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<CreateScopedResult> Handle(CreateScopedCommand command, CancellationToken cancellationToken)
    {
        if (command.ObjectId == Guid.Empty)
            throw new ArgumentException("A non-empty Microsoft object ID is required.", nameof(command));
        var approvalReference = command.IdentityApprovalReference?.Trim();
        if (string.IsNullOrEmpty(approvalReference) || approvalReference.Length > ApprovalReferenceMaxLength)
            throw new ArgumentException("A valid identity approval reference is required.", nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CorrelationId);
        var email = AdminContactEmail.TryNormalize(command.Email, out var normalizedEmail)
            ? normalizedEmail
            : null;

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _admins.AcquireIdentityMutationLockAsync(ct);
            var tenantId = await _tenantBinding.GetRequiredTenantIdAsync(ct);
            if (await _admins.GetByMicrosoftIdentityAsync(tenantId, command.ObjectId, ct) is not null)
                throw new ConflictException("An admin account already exists for the supplied identity details.");

            var now = _clock.UtcNow;
            var account = User.CreateScopedMicrosoft(tenantId, command.ObjectId, email, now);
            _admins.Add(account);
            _audit.Append(Audit.For(
                AuditAction.CreateScoped, command.ActingAdminId, approvalReference, now,
                targetAdminId: account.Id));
            await _unitOfWork.SaveChangesAsync(ct);
            return new CreateScopedResult(account.Id, account.Email);
        }, cancellationToken);
    }
}
