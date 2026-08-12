using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Merchants.Application.Users.Roles;

namespace Merchants.Application.Users;

/// <summary>
/// Approves a merchant user onto a merchant the admin already validated (REQ-6). The Admin permission check
/// (<c>merchants.users.approve</c>) AND the accessible-merchant floor run at the HOST before dispatch (critique B3); this
/// command receives an ALREADY-VALIDATED merchant id and lives in Merchants.Application with NO Admin import. It runs
/// in ONE pol_admin transaction: validate the assigned roles exist + are Active (REQ-6.5), bind the merchant, set the
/// role assignments, flip the user Active, and audit. Idempotent: an already-Active target is a no-op success
/// (REQ-6.4), enforced by <see cref="User.Approve"/> itself. A non-PendingApproval target (Rejected/Suspended —
/// must resubmit first) is a 409 (REQ-6.5).
/// </summary>
public sealed record ApproveCommand(
    string Subject,
    Guid ValidatedMerchantId,
    IReadOnlyList<string> RoleCodes,
    string ActingAdminSubject,
    Guid ActingAdminId,
    string CorrelationId,
    long? ExpectedVersion = null,
    string? IdempotencyKey = null) : ICommand<ApproveResult>;

public sealed record ApproveResult(Guid UserId, UserStatus Status, bool AlreadyActive, long Version = 1);

public sealed class ApproveHandler : ICommandHandler<ApproveCommand, ApproveResult>
{
    private readonly IAccountStore _accounts;
    private readonly IRoleRepository _roles;
    private readonly IRegistrationAuditWriter _audit;
    private readonly IUserUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IInvitationRepository? _invitations;
    private readonly IAdminUserOperationStore? _operations;

    // IAccountStore, never IUserRepository: an admin-plane request has no bound merchant actor, and the
    // PendingApproval target row's MerchantId is NULL — invisible to the filtered repository either way
    // (bugfix-merchant-prebind-wiring F3).
    public ApproveHandler(
        IAccountStore accounts, IRoleRepository roles, IRegistrationAuditWriter audit,
        IUserUnitOfWork unitOfWork, IClock clock,
        IInvitationRepository? invitations = null, IAdminUserOperationStore? operations = null)
    {
        _accounts = accounts;
        _roles = roles;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _invitations = invitations;
        _operations = operations;
    }

    public ValueTask<ApproveResult> Handle(ApproveCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var now = _clock.UtcNow;
            var account = await FindAccountAsync(command.Subject, ct)
                ?? throw new NotFoundException("The merchant-user registration was not found."); // 404 (REQ-22.2)

            var roleCodes = command.RoleCodes
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (roleCodes.Count == 0 && _invitations is not null)
            {
                var invitation = await _invitations.FindAcceptedByUserIdAsync(account.Id, ct);
                roleCodes = invitation?.IntendedRoleCodes().ToList() ?? [];
            }

            const string operation = "merchant-user.approve";
            var intentHash = AdminUserOperationReplay.Hash(new
            {
                command.Subject,
                command.ValidatedMerchantId,
                RoleCodes = roleCodes,
                command.ExpectedVersion,
            });
            if (command.IdempotencyKey is { } idempotencyKey)
            {
                var store = _operations ?? throw new InvalidOperationException("Admin operation store is required.");
                var replay = await store.FindAsync(
                    command.ValidatedMerchantId, command.ActingAdminId, operation, idempotencyKey, ct);
                if (replay is not null)
                    return AdminUserOperationReplay.Read<ApproveResult>(replay, intentHash);
            }
            if (command.ExpectedVersion is { } expectedVersion)
                account.EnsureVersion(expectedVersion);

            // Idempotent no-op for an already-Active target ONLY when bound to the same merchant (REQ-6.4); re-approving
            // onto a different merchant is rejected. User.Approve enforces both — a different-merchant re-approve
            // throws InvalidOperationException (mapped to 409 by ProblemDetailsExceptionHandler), a same-merchant
            // re-approve is a silent no-op — so this branch just detects which one happened for the result payload.
            if (account.Status == UserStatus.Active)
            {
                account.Approve(command.ValidatedMerchantId, now);
                var alreadyActive = new ApproveResult(account.Id, account.Status, AlreadyActive: true, account.Version);
                if (command.IdempotencyKey is { } replayKey)
                {
                    _operations!.Add(AdminUserOperationRecord.Succeeded(
                        command.ValidatedMerchantId, command.ActingAdminId, operation, replayKey,
                        intentHash, JsonSerializer.Serialize(alreadyActive), 200, now));
                    await _unitOfWork.SaveChangesAsync(ct);
                }
                return alreadyActive;
            }

            if (account.Status != UserStatus.PendingApproval)
                throw new ConflictException(
                    $"Cannot approve a merchant user in status {account.Status}; it must be PendingApproval (a rejected user must resubmit first)."); // 409 (REQ-6.5)

            if (roleCodes.Count == 0)
                throw new ArgumentException("At least one role must be assigned at approval."); // 400 (REQ-6.2)

            // Visible (shared + this merchant's own) AND Active in one lookup (REQ-3.5/7.2/6.5) — a code
            // missing from the result is unknown, invisible to this merchant, OR inactive; all three collapse
            // to the same "unknown or inactive" 409 the original two-step check produced.
            var resolved = await _roles.GetActiveRoleIdsByCodesAsync(command.ValidatedMerchantId, roleCodes, ct);
            var unresolved = roleCodes.Where(c => !resolved.ContainsKey(c)).ToList();
            if (unresolved.Count > 0)
                throw new ConflictException($"Role(s) unknown or inactive: {string.Join(", ", unresolved)}."); // 409 (REQ-6.5)
            var roleIds = resolved.Values.ToList();

            account.Approve(command.ValidatedMerchantId, now); // PendingApproval -> Active, sets MerchantId (REQ-6.2/9.2)
            foreach (var roleId in roleIds)
                _roles.AddAssignment(RoleAssignment.Create(
                    account.Id, roleId, command.ValidatedMerchantId, command.ActingAdminId, now));

            _audit.Append(RegistrationAudit.For(RegistrationAuditAction.Approved, account.Subject, command.CorrelationId, now,
                actorSubject: command.ActingAdminSubject, role: string.Join(", ", roleCodes), merchantId: command.ValidatedMerchantId));

            var result = new ApproveResult(account.Id, UserStatus.Active, AlreadyActive: false, account.Version);
            if (command.IdempotencyKey is { } key)
                _operations!.Add(AdminUserOperationRecord.Succeeded(
                    command.ValidatedMerchantId, command.ActingAdminId, operation, key, intentHash,
                    JsonSerializer.Serialize(result), 200, now));
            await _unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken));

    private Task<User?> FindAccountAsync(string subjectOrId, CancellationToken cancellationToken) =>
        Guid.TryParse(subjectOrId, out var id)
            ? _accounts.FindByIdAsync(id, cancellationToken)
            : _accounts.FindBySubjectAsync(subjectOrId, cancellationToken);
}

/// <summary>
/// Rejects a PendingApproval merchant user (REQ-5.1/6). Gated <c>merchants.users.reject</c> at the host. Sets Status Rejected,
/// kills any live sessions of that user (REQ-12.3 — a pending user has none, but defensive), and audits, in ONE
/// pol_admin transaction. A non-PendingApproval target is a 409; an unknown target is a 404 (REQ-22.2).
/// </summary>
public sealed record RejectCommand(
    string Subject, string? Reason, string ActingAdminSubject, string CorrelationId,
    Guid ActingAdminId = default, long? ExpectedVersion = null, string? IdempotencyKey = null)
    : ICommand<RejectResult>;

public sealed record RejectResult(Guid UserId, UserStatus Status, long Version = 1);

public sealed class RejectHandler : ICommandHandler<RejectCommand, RejectResult>
{
    private readonly IAccountStore _accounts;
    private readonly ISessionStore _sessions;
    private readonly IRegistrationAuditWriter _audit;
    private readonly IUserUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IAdminUserOperationStore? _operations;

    // IAccountStore for the same pre-bind reason as ApproveHandler (bugfix-merchant-prebind-wiring F4).
    public RejectHandler(
        IAccountStore accounts, ISessionStore sessions, IRegistrationAuditWriter audit,
        IUserUnitOfWork unitOfWork, IClock clock, IAdminUserOperationStore? operations = null)
    {
        _accounts = accounts;
        _sessions = sessions;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _operations = operations;
    }

    public ValueTask<RejectResult> Handle(RejectCommand command, CancellationToken cancellationToken) =>
        new(_unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var now = _clock.UtcNow;
            var account = await FindAccountAsync(command.Subject, ct)
                ?? throw new NotFoundException("The merchant-user registration was not found."); // 404 (REQ-22.2)

            const string operation = "merchant-user.reject";
            var reason = NormalizeReason(command.Reason);
            var intentHash = AdminUserOperationReplay.Hash(new
            {
                command.Subject,
                MerchantId = account.MerchantId,
                Reason = reason,
                command.ExpectedVersion,
            });
            if (command.IdempotencyKey is { } idempotencyKey)
            {
                if (command.ActingAdminId == Guid.Empty)
                    throw new ArgumentException("Acting Admin is required for idempotent rejection.");
                var store = _operations ?? throw new InvalidOperationException("Admin operation store is required.");
                var replay = await store.FindAsync(
                    account.MerchantId, command.ActingAdminId, operation, idempotencyKey, ct);
                if (replay is not null)
                    return AdminUserOperationReplay.Read<RejectResult>(replay, intentHash);
            }
            if (command.ExpectedVersion is { } expectedVersion)
                account.EnsureVersion(expectedVersion);

            if (account.Status != UserStatus.PendingApproval)
                throw new ConflictException($"Cannot reject a merchant user in status {account.Status}; it must be PendingApproval."); // 409

            account.Reject(now); // PendingApproval -> Rejected (REQ-5.1)
            await _sessions.RevokeAllForUserAsync(account.Id, ct); // kill any live sessions (REQ-12.3)

            _audit.Append(RegistrationAudit.For(RegistrationAuditAction.Rejected, account.Subject, command.CorrelationId, now,
                actorSubject: command.ActingAdminSubject, reason: reason)); // record the rationale (REQ-5.1)

            var result = new RejectResult(account.Id, UserStatus.Rejected, account.Version);
            if (command.IdempotencyKey is { } key)
                _operations!.Add(AdminUserOperationRecord.Succeeded(
                    account.MerchantId, command.ActingAdminId, operation, key, intentHash,
                    JsonSerializer.Serialize(result), 200, now));
            await _unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken));

    private Task<User?> FindAccountAsync(string subjectOrId, CancellationToken cancellationToken) =>
        Guid.TryParse(subjectOrId, out var id)
            ? _accounts.FindByIdAsync(id, cancellationToken)
            : _accounts.FindBySubjectAsync(subjectOrId, cancellationToken);

    // Blank -> NULL (no rationale given); trim + cap to the audit column width (REQ-5.1).
    private static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;
        var trimmed = reason.Trim();
        return trimmed.Length <= 1024 ? trimmed : trimmed[..1024];
    }
}

internal static class AdminUserOperationReplay
{
    public static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    public static T Read<T>(AdminUserOperationRecord record, string expectedHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.IntentHash), Encoding.ASCII.GetBytes(expectedHash)))
            throw new ConflictException(
                "Idempotency key was reused with a different intent.", "idempotency_key_reused");
        return JsonSerializer.Deserialize<T>(record.Result)
            ?? throw new InvalidOperationException("Stored Admin user operation result is invalid.");
    }
}
