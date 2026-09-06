using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Governance.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain;
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

    public bool CanHandle(string targetType) => targetType is "psp-credential-version" or "routing-ruleset";

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
        if (decision.Decision == "approved")
        {
            var snapshot = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == connectionId && x.MerchantId == merchantId, ct), cancellationToken)
                ?? throw new NotFoundException("PSP connection was not found.");
            EnsureApproval(snapshot.PendingApprovalId, snapshot.Version, decision);
            var candidateId = snapshot.PendingSecretVersionId
                ?? throw new InvalidOperationException("PSP credential candidate is missing.");
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
