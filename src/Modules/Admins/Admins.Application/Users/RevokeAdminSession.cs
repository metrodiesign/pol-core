using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admins.Application.Users;

/// <summary>A Super revokes a session of another admin (admin-account-management REQ-5). The route admin is
/// resolved first (<c>PlatformUserSessions</c> has no FK to <c>PlatformUsers</c>, so a revoke is never accepted against a
/// nonexistent admin); an unknown session OR one owned by a different admin -> 404 (no existence leak). The ENTIRE
/// rotation family is revoked — a single-row revoke would leave the rotated successor live (REQ-5.1). The revoke
/// and the audit run in ONE keyed "admin" transaction (REQ-5.2); the result surfaces the revoked
/// <see cref="RevokeSessionResult.FamilyId"/> so the HOST can emit the structured security-log line (the
/// Application layer stays logging-free by project convention). Idempotent when the family is already revoked.
/// Super-only at the host.</summary>
public sealed record RevokeSessionCommand(
    Guid TargetAdminId, Guid SessionId, Guid ActingAdminId, string CorrelationId, string IdempotencyKey)
    : ICommand<RevokeSessionResult>;

public sealed record RevokeSessionResult(Guid AdminId, Guid SessionId, Guid FamilyId);

public sealed class RevokeSessionHandler : ICommandHandler<RevokeSessionCommand, RevokeSessionResult>
{
    private readonly IUserRepository _admins;
    private readonly ISessionStore _sessions;
    private readonly IAuditWriter _audit;
    private readonly IAdminOperationStore _operations;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RevokeSessionHandler(
        IUserRepository admins,
        ISessionStore sessions,
        IAuditWriter audit,
        IAdminOperationStore operations,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _sessions = sessions;
        _audit = audit;
        _operations = operations;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<RevokeSessionResult> Handle(RevokeSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey);
        const string operation = "RevokePlatformUserSession";
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{command.TargetAdminId:D}\n{command.SessionId:D}"))).ToLowerInvariant();

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _operations.AcquireAsync(command.ActingAdminId, operation, command.IdempotencyKey, ct);
            var prior = await _operations.FindAsync(
                command.ActingAdminId, operation, command.IdempotencyKey, ct);
            if (prior is not null)
            {
                if (!string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new ConflictException(
                        "The idempotency key was reused for a different session revoke.",
                        "idempotency_key_reused");
                if (prior.InProgress || prior.ResponseBody is null)
                    throw new ConflictException("The session revoke is still in progress.", "operation_in_progress");
                return JsonSerializer.Deserialize<RevokeSessionResult>(prior.ResponseBody)
                    ?? throw new InvalidOperationException("Recorded session-revoke response is invalid.");
            }

            // Route admin must exist — there is no FK from PlatformUserSessions to PlatformUsers (REQ-5.4). Existence-only.
            if (!await _admins.ExistsAsync(command.TargetAdminId, ct))
                throw new NotFoundException("The admin account was not found.");

            var session = await _sessions.FindByIdAsync(command.SessionId, ct);
            if (session is null || session.AdminUserId != command.TargetAdminId)
                throw new NotFoundException("The session was not found.");   // no existence leak (REQ-5.4)

            // Revoke the WHOLE family (the rotated successor would otherwise stay live). Set-based ExecuteUpdate
            // enrolled in this transaction; a no-op when the family is already revoked (idempotent, REQ-5.5).
            await _sessions.RevokeFamilyAsync(session.FamilyId, ct);
            _audit.Append(Audit.For(
                AuditAction.SessionRevoke, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.TargetAdminId));
            // FamilyId flows to the host, which logs sessionId/familyId/targetAdminId/correlationId (REQ-5.2).
            var result = new RevokeSessionResult(command.TargetAdminId, command.SessionId, session.FamilyId);
            _operations.AddSucceeded(
                command.ActingAdminId, operation, command.IdempotencyKey, requestHash,
                JsonSerializer.Serialize(result), _clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
    }
}
