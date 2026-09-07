using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Governance.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Domain.Routing;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class AdminPaymentsApprovalExecutor(
    MerchantRuntimeDbContext db,
    IClock clock,
    IUnitOfWork unitOfWork,
    IVaultSecretStore vault,
    IPspAdapterFactory adapterFactory,
    ISecurityTelemetry telemetry,
    PaymentAuthorizationSqlLockManager authorizationLocks) : IApprovalDecisionExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string targetType) =>
        targetType is "psp-credential-version" or "routing-ruleset" or "merchant-environment";

    public async Task ExecuteAsync(ApprovalDecided decision, CancellationToken cancellationToken)
    {
        try
        {
            if (decision.MerchantId is not { } merchantId || merchantId == Guid.Empty)
                throw new InvalidOperationException("Merchant approval is missing its merchant.");
            if (!Guid.TryParse(decision.TargetId, out var targetId) || targetId == Guid.Empty)
                throw new InvalidOperationException("Approval target identifier is invalid.");

            if (await WasExecutedAsync(decision, merchantId, cancellationToken))
                return;

            if (decision.TargetType == "routing-ruleset")
                await ExecuteRoutingAsync(decision, merchantId, targetId, cancellationToken);
            else if (decision.TargetType == "merchant-environment")
                await ExecuteEnvironmentAsync(decision, merchantId, cancellationToken);
            else
                await ExecuteCredentialAsync(decision, merchantId, targetId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            telemetry.Emit(new DenialEvent(
                DenialCategory.AdminRevalidationDenial, "admin", decision.CheckerId, decision.MerchantId,
                nameof(ApprovalExecutionRecord), "AdminPaymentsApprovalExecutor.Execute",
                "Approval execution was denied or rolled back.", decision.CorrelationId, clock.UtcNow));
            throw;
        }
    }

    private async Task ExecuteRoutingAsync(
        ApprovalDecided decision, Guid merchantId, Guid rulesetId, CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await authorizationLocks.AcquireMerchantExclusiveAsync(merchantId, ct);
            if (await WasExecutedAsync(decision, merchantId, ct))
                return true;

            var execution = Claim(decision, merchantId);
            var ruleset = await PlatformReadGuard.ReadAsync(token => db.RoutingRulesets.Include(x => x.Rules)
                .SingleOrDefaultAsync(x => x.Id == rulesetId && x.MerchantId == merchantId, token), ct)
                ?? throw new NotFoundException("Routing ruleset was not found.");
            EnsureApproval(ruleset.ApprovalId, ruleset.Version, decision);

            if (decision.Decision == "rejected")
            {
                ruleset.ReturnToDraft(clock.UtcNow);
                Complete(execution, decision, succeeded: false, "routing_rejected", $"v{ruleset.Version}");
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }
            if (decision.Decision != "approved")
                throw new InvalidOperationException("Approval decision is invalid.");

            RoutingRuleset.Validate(ruleset.Rules.Select(x => new RoutingRuleSpec(
                x.Priority, x.Method, x.OriginatorId, x.MinAmount, x.MaxAmount,
                x.TargetConnectionId, x.FallbackConnectionId, x.Enabled)).ToList());

            var active = await PlatformReadGuard.ReadAsync(token => db.RoutingRulesets
                .Where(x => x.MerchantId == merchantId && x.Status == RoutingRulesetStatus.Active && x.Id != ruleset.Id)
                .ToListAsync(token), ct);
            foreach (var prior in active)
                prior.Supersede(clock.UtcNow);
            if (active.Count > 0)
                await unitOfWork.SaveChangesAsync(ct);

            ruleset.Activate(clock.UtcNow);
            Complete(execution, decision, succeeded: true, "routing_activated", $"v{ruleset.Version}");
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private async Task ExecuteCredentialAsync(
        ApprovalDecided decision, Guid merchantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var probeSucceeded = false;
        var candidateExpired = false;
        if (decision.Decision == "approved")
        {
            var snapshot = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == connectionId && x.MerchantId == merchantId, ct), cancellationToken)
                ?? throw new NotFoundException("PSP connection was not found.");
            EnsureApproval(snapshot.PendingApprovalId, snapshot.Version, decision);
            var candidateId = snapshot.PendingSecretVersionId
                ?? throw new InvalidOperationException("PSP credential candidate is missing.");
            // A candidate that outlived its 24h staging window is discarded with a distinct outcome rather than
            // charged to the probe: the vault would refuse to read it anyway (critical #7, AC-6.6).
            var expiry = await vault.StagedVersionExpiresAtAsync(merchantId, candidateId, cancellationToken);
            candidateExpired = expiry is { } e && e <= clock.UtcNow;
            if (!candidateExpired)
            {
                try
                {
                    var secret = await vault.ReadVersionForServerAsync(merchantId, candidateId, cancellationToken);
                    await adapterFactory.For(snapshot.Psp).TestConnectionAsync(secret,
                        snapshot.PendingSecretEnvironment ?? snapshot.ActiveSecretEnvironment, cancellationToken);
                    probeSucceeded = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    probeSucceeded = false;
                }
            }
        }

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await authorizationLocks.AcquireMerchantExclusiveAsync(merchantId, ct);
            if (await WasExecutedAsync(decision, merchantId, ct))
                return true;

            var execution = Claim(decision, merchantId);
            var connection = await PlatformReadGuard.ReadAsync(token => db.PspConnections.SingleOrDefaultAsync(
                x => x.Id == connectionId && x.MerchantId == merchantId, token), ct)
                ?? throw new NotFoundException("PSP connection was not found.");
            EnsureApproval(connection.PendingApprovalId, connection.Version, decision);

            if (decision.Decision == "rejected")
            {
                var rejected = connection.RejectPendingSecretVersion();
                await vault.DiscardVersionAsync(merchantId, rejected, ct);
                Complete(execution, decision, succeeded: false, "psp_credentials_rejected", $"v{connection.Version}");
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }
            if (decision.Decision != "approved")
                throw new InvalidOperationException("Approval decision is invalid.");

            var candidateId = connection.PendingSecretVersionId
                ?? throw new InvalidOperationException("PSP credential candidate is missing.");
            if (candidateExpired)
            {
                var expired = connection.RejectPendingSecretVersion();
                await vault.DiscardVersionAsync(merchantId, expired, ct);
                Complete(execution, decision, succeeded: false, "credential_candidate_expired", $"v{connection.Version}");
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }
            if (!probeSucceeded)
            {
                var rejected = connection.RejectPendingSecretVersion();
                await vault.DiscardVersionAsync(merchantId, rejected, ct);
                Complete(execution, decision, succeeded: false, "psp_probe_failed", $"v{connection.Version}");
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }

            var previous = connection.ActiveSecretVersionId;
            if (previous is { } oldVersion)
            {
                await vault.RetireVersionAsync(merchantId, oldVersion, ct);
                await unitOfWork.SaveChangesAsync(ct);
            }
            await vault.ActivateVersionAsync(merchantId, candidateId, ct);
            connection.ActivatePendingSecretVersion();
            Complete(execution, decision, succeeded: true, "psp_credentials_activated", $"v{connection.Version}");
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private async Task ExecuteEnvironmentAsync(
        ApprovalDecided decision, Guid merchantId, CancellationToken cancellationToken)
    {
        // Read-only pre-check: a candidate that outlived its 24h staging window fails the whole request with a
        // distinct outcome rather than being charged to activation (critical #7, AC-7.4). The vault would refuse
        // to read it anyway. Completeness is re-checked INSIDE the transaction, under the lock.
        var candidateExpired = false;
        if (decision.Decision == "approved")
        {
            var snapshot = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.AsNoTracking()
                .Where(x => x.MerchantId == merchantId && x.PendingApprovalId == decision.ApprovalId)
                .ToListAsync(ct), cancellationToken);
            foreach (var connection in snapshot)
            {
                if (connection.PendingSecretVersionId is not { } versionId)
                    continue;
                var expiry = await vault.StagedVersionExpiresAtAsync(merchantId, versionId, cancellationToken);
                if (expiry is { } e && e <= clock.UtcNow)
                {
                    candidateExpired = true;
                    break;
                }
            }
        }

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await authorizationLocks.AcquireMerchantExclusiveAsync(merchantId, ct);
            if (await WasExecutedAsync(decision, merchantId, ct))
                return true;

            var execution = Claim(decision, merchantId);
            var merchant = await PlatformReadGuard.ReadAsync(token => db.Merchants
                .SingleOrDefaultAsync(x => x.Id == merchantId, token), ct)
                ?? throw new NotFoundException("Merchant was not found.");
            // A mismatched approval id is the WRONG event (throw so the dispatcher routes it elsewhere). A
            // matching id whose merchant version moved is STALE — handled below as a terminal precondition, not a
            // throw, so the candidates are cleaned up and the event is not retried forever.
            if (merchant.PendingPaymentEnvironmentApprovalId != decision.ApprovalId)
                throw new ConcurrencyConflictException("Approval target version changed.");
            var connections = await PlatformReadGuard.ReadAsync(token => db.PspConnections
                .Where(x => x.MerchantId == merchantId).ToListAsync(token), ct);
            var candidates = connections.Where(x => x.PendingApprovalId == decision.ApprovalId).ToList();

            if (decision.Decision == "rejected")
            {
                await DiscardCandidatesAsync(merchantId, candidates, ct);
                merchant.RejectPendingPaymentEnvironment();
                Complete(execution, decision, succeeded: false, "environment_rejected", $"v{merchant.Version}");
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }
            if (decision.Decision != "approved")
                throw new InvalidOperationException("Approval decision is invalid.");

            // Deterministic precondition failures (candidate expired; the merchant version moved since the
            // request so the approval is stale; or a connection is missing its staged candidate — e.g. one
            // created AFTER the request) are terminal outcomes that clear pending state, discard every candidate
            // (AC-7.3 covers "reject OR stale") and COMMIT: rolling back would drop the claim and the dispatcher
            // would retry the same event forever because the precondition never changes on its own (design 250-263).
            var stale = decision.TargetVersion != $"v{merchant.Version}";
            var incomplete = connections.Any(x =>
                x.PendingApprovalId != decision.ApprovalId || x.PendingSecretVersionId is null);
            if (candidateExpired || stale || incomplete)
            {
                await DiscardCandidatesAsync(merchantId, candidates, ct);
                merchant.RejectPendingPaymentEnvironment();
                Complete(execution, decision, succeeded: false,
                    candidateExpired ? "credential_candidate_expired"
                        : stale ? "environment_stale" : "environment_credentials_incomplete",
                    $"v{merchant.Version}");
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }

            // Activate every connection's candidate AND flip the merchant environment in this one transaction. An
            // unexpected failure mid-activation (e.g. the vault throwing) propagates out and rolls the whole
            // transaction back — no connection is left live while another stays sandbox (REQ-2.14/2.15, critical #2).
            foreach (var connection in connections)
            {
                var candidateId = connection.PendingSecretVersionId
                    ?? throw new InvalidOperationException("PSP credential candidate is missing.");
                if (connection.ActiveSecretVersionId is { } previous)
                {
                    await vault.RetireVersionAsync(merchantId, previous, ct);
                    await unitOfWork.SaveChangesAsync(ct);
                }
                await vault.ActivateVersionAsync(merchantId, candidateId, ct);
                connection.ActivatePendingSecretVersion();
            }
            merchant.ActivatePendingPaymentEnvironment(clock.UtcNow);
            Complete(execution, decision, succeeded: true, "environment_activated", $"v{merchant.Version}");
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private async Task DiscardCandidatesAsync(
        Guid merchantId, IReadOnlyList<Connection> candidates, CancellationToken ct)
    {
        foreach (var connection in candidates)
        {
            var discarded = connection.RejectPendingSecretVersion();
            await vault.DiscardVersionAsync(merchantId, discarded, ct);
        }
    }

    private ApprovalExecutionRecord Claim(ApprovalDecided decision, Guid merchantId)
    {
        var execution = ApprovalExecutionRecord.Claim(
            decision.EventId, decision.ApprovalId, merchantId, decision.TargetType,
            decision.TargetId, decision.Decision, clock.UtcNow);
        db.ApprovalExecutionRecords.Add(execution);
        return execution;
    }

    private void Complete(
        ApprovalExecutionRecord execution,
        ApprovalDecided decision,
        bool succeeded,
        string outcome,
        string? version)
    {
        execution.Complete(succeeded, outcome, clock.UtcNow);
        var message = new ApprovalExecutionReported(
            Guid.CreateVersion7(), decision.ApprovalId, decision.CheckerId,
            succeeded, Unknown: false, outcome, version, decision.MerchantId,
            decision.TargetType, decision.TargetId, decision.CorrelationId, clock.UtcNow);
        db.OutboxMessages.Add(OutboxMessage.Create(
            message.EventId, decision.MerchantId!.Value, ApprovalExecutionReported.EventType,
            ApprovalExecutionReported.SchemaVersion, JsonSerializer.Serialize(message, Json), message.OccurredAt));
    }

    private Task<bool> WasExecutedAsync(
        ApprovalDecided decision, Guid merchantId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => db.ApprovalExecutionRecords.AsNoTracking().AnyAsync(x =>
            x.MerchantId == merchantId && (x.EventId == decision.EventId || x.ApprovalId == decision.ApprovalId), ct),
            cancellationToken);

    private static void EnsureApproval(Guid? approvalId, long version, ApprovalDecided decision)
    {
        if (approvalId != decision.ApprovalId || decision.TargetVersion != $"v{version}")
            throw new ConcurrencyConflictException("Approval target version changed.");
    }
}
