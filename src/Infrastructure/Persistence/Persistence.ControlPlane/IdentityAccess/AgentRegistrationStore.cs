using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Access.Domain;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using Contracts;
using Governance.Domain;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Persistence.ControlPlane.Governance;

namespace Persistence.ControlPlane.IdentityAccess;

internal sealed class AgentRegistrationStore(
    ControlPlaneDbContext db,
    IClock clock,
    [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
    GovernanceSqlLockManager locks) : IAgentRegistrationStore
{

    public Task<RegistrationSession?> FindSessionAsync(
        byte[] sessionReferenceHash, CancellationToken cancellationToken) =>
        db.RegistrationSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionReferenceHash == sessionReferenceHash, cancellationToken);

    public Task<AgentRegistration?> FindCaseAsync(
        ExternalIdentity identity, Guid merchantId, CancellationToken cancellationToken) =>
        db.AgentRegistrations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Provider == identity.Provider && x.TenantId == identity.TenantId
            && x.ExternalUserId == identity.ExternalUserId && x.MerchantId == merchantId, cancellationToken);

    public Task<AgentRegistration?> FindCaseByIdAsync(Guid registrationId, CancellationToken cancellationToken) =>
        db.AgentRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == registrationId, cancellationToken);

    public async Task<AgentRegistration> SaveDraftAsync(
        RegistrationSession session, RegistrationDraftRequest draft, long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var identity = new ExternalIdentity(session.Provider, session.TenantId, session.ExternalUserId);
        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireAsync(IdentityLock(identity), ct);
            var registration = await db.AgentRegistrations.SingleOrDefaultAsync(x =>
                x.Provider == identity.Provider && x.TenantId == identity.TenantId
                && x.ExternalUserId == identity.ExternalUserId, ct);
            if (registration is null)
            {
                registration = AgentRegistration.Create(session.MerchantId, identity, draft.SaleCode,
                    draft.Email, draft.PhoneNumber, draft.Profile.GetRawText(), clock.UtcNow);
                db.AgentRegistrations.Add(registration);
            }
            else
            {
                if (registration.MerchantId != session.MerchantId)
                    throw new ConflictException("The registration is bound to another merchant.", "registration_merchant_mismatch");
                EnsureVersion(registration.Version, expectedVersion);
                registration.UpdateDraft(draft.SaleCode, draft.Email, draft.PhoneNumber,
                    draft.Profile.GetRawText(), clock.UtcNow);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return registration;
        }, cancellationToken);
    }

    public async Task<RegistrationSubmitResult> SubmitAsync(
        RegistrationSession session, string idempotencyKey, long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var identity = new ExternalIdentity(session.Provider, session.TenantId, session.ExternalUserId);
        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireAsync(IdentityLock(identity), ct);
            var registration = await db.AgentRegistrations.SingleOrDefaultAsync(x =>
                x.Provider == identity.Provider && x.TenantId == identity.TenantId
                && x.ExternalUserId == identity.ExternalUserId, ct)
                ?? throw new NotFoundException("Registration draft was not found.");
            if (registration.MerchantId != session.MerchantId)
                throw new ConflictException("The registration is bound to another merchant.", "registration_merchant_mismatch");
            EnsureVersion(registration.Version, expectedVersion);

            var intentHash = HashIntent(registration);
            var existing = await db.AgentRegistrationAttempts.SingleOrDefaultAsync(x =>
                x.RegistrationId == registration.Id && x.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                if (!existing.MatchesIntent(idempotencyKey, intentHash))
                    throw new ConflictException("The idempotency key was reused for a different draft.", "idempotency_conflict");
                return new RegistrationSubmitResult(ToCaseView(registration, existing), ToAttemptView(existing), true);
            }

            if (registration.Status == AgentRegistrationStatus.Pending)
                throw new ConflictException("A registration attempt is already pending.", "registration_pending");
            if (registration.Status == AgentRegistrationStatus.Approved)
                throw new ConflictException("The identity already has an approved account.", "account_already_approved");

            var sale = await ReadCurrentSaleAsync(registration.MerchantId, registration.SaleCode, ct)
                ?? throw new ConflictException("The sale is not available for this registration.", "registration_sale_invalid");
            var attempt = AgentRegistrationAttempt.Create(
                registration.Id, registration.MerchantId, registration.CurrentAttemptNo + 1, identity,
                registration.SaleCode, sale.SaleId, sale.BranchId, sale.SaleVersion, sale.BranchVersion,
                registration.Email, registration.PhoneNumber, registration.ProfileJson, idempotencyKey,
                intentHash, clock.UtcNow);
            registration.StartAttempt(attempt.Id, attempt.AttemptNo, clock.UtcNow);
            db.AgentRegistrationAttempts.Add(attempt);
            await unitOfWork.SaveChangesAsync(ct);
            return new RegistrationSubmitResult(ToCaseView(registration, attempt), ToAttemptView(attempt), false);
        }, cancellationToken);
    }

    public async Task<RegistrationDecisionResult> ApproveAsync(
        Guid registrationId, Guid attemptId, Guid reviewerAccountId, string contactEvidenceReference,
        string idempotencyKey, long? expectedVersion, CancellationToken cancellationToken)
    {
        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireAsync(RegistrationLock(registrationId), ct);
            var registration = await db.AgentRegistrations.SingleOrDefaultAsync(x => x.Id == registrationId, ct)
                ?? throw new NotFoundException("Registration was not found.");
            var attempt = await db.AgentRegistrationAttempts.SingleOrDefaultAsync(x =>
                x.Id == attemptId && x.RegistrationId == registrationId, ct)
                ?? throw new NotFoundException("Registration attempt was not found.");
            var decisionHash = HashDecision("approved", contactEvidenceReference, null);
            if (attempt.DecisionIdempotencyKey is not null)
            {
                if (!attempt.MatchesDecisionIntent(idempotencyKey, decisionHash))
                    throw new ConflictException("The attempt was already decided.", "decision_already_recorded");
                return new RegistrationDecisionResult(ToCaseView(registration, attempt), ToAttemptView(attempt), true);
            }
            EnsureVersion(registration.Version, expectedVersion);
            EnsureCurrentPending(registration, attempt);

            var sale = await ReadCurrentSaleAsync(registration.MerchantId, attempt.SaleCode, ct)
                ?? throw new ConflictException("The sale or branch changed after submit.", "registration_context_changed");
            if (sale.SaleId != attempt.SaleId || sale.BranchId != attempt.BranchId
                || sale.SaleVersion != attempt.SaleVersion || sale.BranchVersion != attempt.BranchVersion)
                throw new ConflictException("The sale or branch changed after submit.", "registration_context_changed");
            if (await db.Agents.AnyAsync(x => x.SaleId == sale.SaleId, ct))
                throw new ConflictException("The sale is already assigned to another agent.", "sale_already_bound");
            if (await db.LoginAccounts.AnyAsync(x => x.Provider == attempt.Provider
                    && x.TenantId == attempt.TenantId && x.ExternalUserId == attempt.ExternalUserId, ct))
                throw new ConflictException("The identity already has an account.", "identity_already_bound");

            var role = await db.Roles.SingleOrDefaultAsync(x =>
                x.Code == "merchant_staff" && x.Scope == global::Iam.Domain.Permissions.Scope.Shared
                && x.MerchantId == null && x.Status == RoleStatus.Active, ct)
                ?? throw new ConflictException("The initial merchant role is unavailable.", "registration_role_unavailable");

            var now = clock.UtcNow;
            attempt.SetDecisionIdempotency(idempotencyKey, decisionHash);
            attempt.Approve(reviewerAccountId, contactEvidenceReference, now);
            registration.ApplyDecision(attempt.Id, AgentRegistrationAttemptStatus.Approved, now);
            var displayName = DisplayName(attempt.ProfileJson, attempt.Email);
            var account = Account.Create(AccountType.Agent, displayName, now);
            db.Accounts.Add(account);
            db.LoginAccounts.Add(LoginAccount.Create(account.Id,
                new ExternalIdentity(attempt.Provider, attempt.TenantId, attempt.ExternalUserId),
                attempt.Email, displayName, now));
            db.Agents.Add(Agent.Create(account.Id, attempt.MerchantId, attempt.SaleId, attempt.ProfileJson));
            var access = MerchantAccess.Create(account.Id, attempt.MerchantId, DataScope.Self);
            db.AccountMerchantAccess.Add(access);
            db.AccessRoles.Add(AccessRole.Create(access.Id, attempt.MerchantId, role.Id));
            db.GovernanceOutboxMessages.Add(CreateDecisionOutbox(attempt, "approved", now));
            await unitOfWork.SaveChangesAsync(ct);
            return new RegistrationDecisionResult(ToCaseView(registration, attempt), ToAttemptView(attempt), false);
        }, cancellationToken);
    }

    public async Task<RegistrationDecisionResult> RejectAsync(
        Guid registrationId, Guid attemptId, Guid reviewerAccountId, string rejectionReason,
        string? internalReviewNote, string idempotencyKey, long? expectedVersion, CancellationToken cancellationToken)
    {
        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireAsync(RegistrationLock(registrationId), ct);
            var registration = await db.AgentRegistrations.SingleOrDefaultAsync(x => x.Id == registrationId, ct)
                ?? throw new NotFoundException("Registration was not found.");
            var attempt = await db.AgentRegistrationAttempts.SingleOrDefaultAsync(x =>
                x.Id == attemptId && x.RegistrationId == registrationId, ct)
                ?? throw new NotFoundException("Registration attempt was not found.");
            var decisionHash = HashDecision("rejected", rejectionReason, internalReviewNote);
            if (attempt.DecisionIdempotencyKey is not null)
            {
                if (!attempt.MatchesDecisionIntent(idempotencyKey, decisionHash))
                    throw new ConflictException("The attempt was already decided.", "decision_already_recorded");
                return new RegistrationDecisionResult(ToCaseView(registration, attempt), ToAttemptView(attempt), true);
            }
            EnsureVersion(registration.Version, expectedVersion);
            EnsureCurrentPending(registration, attempt);
            var now = clock.UtcNow;
            attempt.SetDecisionIdempotency(idempotencyKey, decisionHash);
            attempt.Reject(reviewerAccountId, rejectionReason, internalReviewNote, now);
            registration.ApplyDecision(attempt.Id, AgentRegistrationAttemptStatus.Rejected, now);
            db.GovernanceOutboxMessages.Add(CreateDecisionOutbox(attempt, "rejected", now));
            await unitOfWork.SaveChangesAsync(ct);
            return new RegistrationDecisionResult(ToCaseView(registration, attempt), ToAttemptView(attempt), false);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRegistrationAttempt>> ListAttemptsAsync(
        Guid registrationId, CancellationToken cancellationToken) =>
        await db.AgentRegistrationAttempts.AsNoTracking().Where(x => x.RegistrationId == registrationId)
            .OrderBy(x => x.AttemptNo).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentRegistration>> ListCasesAsync(
        Guid merchantId, CancellationToken cancellationToken) =>
        await db.AgentRegistrations.AsNoTracking().Where(x => x.MerchantId == merchantId)
            .OrderByDescending(x => x.UpdatedAt).ToListAsync(cancellationToken);

    private async Task<SaleSnapshot?> ReadCurrentSaleAsync(Guid merchantId, string saleCode, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT TOP (1) s.Id AS SaleId, s.BranchId, s.Version AS SaleVersion, b.Version AS BranchVersion
            FROM merch.Sales s
            INNER JOIN merch.Branches b ON b.MerchantId = s.MerchantId AND b.Id = s.BranchId
            WHERE s.MerchantId = @merchantId AND s.Code = @saleCode
              AND s.Status = 1 AND b.Status = 1;
            """;
        AddParameter(command, "@merchantId", merchantId);
        AddParameter(command, "@saleCode", saleCode);
        if (db.Database.CurrentTransaction?.GetDbTransaction() is { } transaction)
            command.Transaction = transaction;
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return !await reader.ReadAsync(ct)
            ? null
            : new SaleSnapshot(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private GovernanceOutboxMessage CreateDecisionOutbox(AgentRegistrationAttempt attempt, string decision, DateTime now)
    {
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new AgentRegistrationDecidedV1(
            eventId, attempt.RegistrationId, attempt.Id, attempt.MerchantId, decision,
            attempt.Email, attempt.PhoneNumber, attempt.RejectionReason, now));
        return GovernanceOutboxMessage.Create(Guid.CreateVersion7(), GovernanceScopeKind.Merchant,
            attempt.MerchantId, AgentRegistrationDecidedV1.EventType, AgentRegistrationDecidedV1.SchemaVersion,
            payload, now);
    }

    private static string IdentityLock(ExternalIdentity identity) =>
        $"agent-registration:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identity.Provider}\0{identity.TenantId}\0{identity.ExternalUserId}")))}";

    private static string RegistrationLock(Guid registrationId) => $"agent-registration:{registrationId:D}";

    private static void EnsureVersion(long actual, long? expected)
    {
        if (expected is not null && actual != expected.Value)
            throw new ConcurrencyConflictException("The registration changed concurrently.");
    }

    private static void EnsureCurrentPending(AgentRegistration registration, AgentRegistrationAttempt attempt)
    {
        if (registration.Status != AgentRegistrationStatus.Pending
            || registration.CurrentAttemptId != attempt.Id
            || attempt.Status != AgentRegistrationAttemptStatus.Pending)
            throw new ConflictException("Only the current pending attempt can be decided.", "registration_not_pending");
    }

    private static string HashIntent(AgentRegistration registration) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{registration.SaleCode}\0{registration.Email}\0{registration.PhoneNumber}\0{registration.ProfileJson}")))
            .ToLowerInvariant();

    private static string HashDecision(string decision, string reason, string? internalReviewNote) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{decision}\0{reason.Trim()}\0{internalReviewNote?.Trim()}"))).ToLowerInvariant();

    private static string DisplayName(string profileJson, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(profileJson);
            if (document.RootElement.TryGetProperty("displayName", out var value)
                && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        }
        catch (JsonException)
        {
            // Draft validation owns the shape; the fallback keeps approval safe if a legacy row is malformed.
        }
        return fallback;
    }

    private static RegistrationCaseView ToCaseView(AgentRegistration registration, AgentRegistrationAttempt attempt) =>
        AgentRegistrationService.ToView(registration, attempt.RejectionReason);

    private static RegistrationAttemptView ToAttemptView(AgentRegistrationAttempt attempt) =>
        AgentRegistrationService.ToReviewerAttemptView(attempt) with { Version = attempt.Version };

    private sealed record SaleSnapshot(Guid SaleId, Guid BranchId, long SaleVersion, long BranchVersion);
}
