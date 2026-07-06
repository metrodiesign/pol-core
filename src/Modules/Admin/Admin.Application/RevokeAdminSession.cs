using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Admin.Application.RevokeAdminSession;

/// <summary>A Super revokes a session of another admin (admin-account-management REQ-5). The route admin is
/// resolved first (<c>AdminSessions</c> has no FK to <c>AdminAccounts</c>, so a revoke is never accepted against a
/// nonexistent admin); an unknown session OR one owned by a different admin -> 404 (no existence leak). The ENTIRE
/// rotation family is revoked — a single-row revoke would leave the rotated successor live (REQ-5.1). The revoke
/// and the audit run in ONE keyed "admin" transaction (REQ-5.2); the result surfaces the revoked
/// <see cref="RevokeAdminSessionResult.FamilyId"/> so the HOST can emit the structured security-log line (the
/// Application layer stays logging-free by project convention). Idempotent when the family is already revoked.
/// Super-only at the host.</summary>
public sealed record RevokeAdminSessionCommand(Guid TargetAdminId, Guid SessionId, Guid ActingAdminId, string CorrelationId)
    : ICommand<RevokeAdminSessionResult>;

public sealed record RevokeAdminSessionResult(Guid AdminId, Guid SessionId, Guid FamilyId);

public sealed class RevokeAdminSessionHandler : ICommandHandler<RevokeAdminSessionCommand, RevokeAdminSessionResult>
{
    private readonly IAdminAccountRepository _admins;
    private readonly IAdminSessionStore _sessions;
    private readonly IAdminAccountAuditWriter _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RevokeAdminSessionHandler(
        IAdminAccountRepository admins,
        IAdminSessionStore sessions,
        IAdminAccountAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _sessions = sessions;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<RevokeAdminSessionResult> Handle(RevokeAdminSessionCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Route admin must exist — there is no FK from AdminSessions to AdminAccounts (REQ-5.4). Existence-only.
            if (!await _admins.ExistsAsync(command.TargetAdminId, ct))
                throw new NotFoundException("The admin account was not found.");

            var session = await _sessions.FindByIdAsync(command.SessionId, ct);
            if (session is null || session.AdminAccountId != command.TargetAdminId)
                throw new NotFoundException("The session was not found.");   // no existence leak (REQ-5.4)

            // Revoke the WHOLE family (the rotated successor would otherwise stay live). Set-based ExecuteUpdate
            // enrolled in this transaction; a no-op when the family is already revoked (idempotent, REQ-5.5).
            await _sessions.RevokeFamilyAsync(session.FamilyId, ct);
            _audit.Append(AdminAccountAudit.For(
                AdminAuditAction.SessionRevoke, command.ActingAdminId, command.CorrelationId, _clock.UtcNow,
                targetAdminId: command.TargetAdminId));
            await _unitOfWork.SaveChangesAsync(ct);
            // FamilyId flows to the host, which logs sessionId/familyId/targetAdminId/correlationId (REQ-5.2).
            return new RevokeAdminSessionResult(command.TargetAdminId, command.SessionId, session.FamilyId);
        }, cancellationToken);
    }
}
