using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Governance.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain.Routing;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class AdminPaymentsApprovalExecutor(
    MerchantRuntimeDbContext db,
    IClock clock,
    IUnitOfWork unitOfWork,
    IVaultSecretStore vault,
    IPspAdapterFactory adapterFactory) : IApprovalDecisionExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string targetType) => targetType is "psp-credential-version" or "routing-ruleset";

    public async Task ExecuteAsync(ApprovalDecided decision, CancellationToken cancellationToken)
    {
        if (decision.MerchantId is not { } merchantId || merchantId == Guid.Empty)
            throw new InvalidOperationException("Merchant approval is missing its merchant.");
        if (!Guid.TryParse(decision.TargetId, out var targetId) || targetId == Guid.Empty)
            throw new InvalidOperationException("Approval target identifier is invalid.");

        if (decision.TargetType == "routing-ruleset")
            await ExecuteRoutingAsync(decision, merchantId, targetId, cancellationToken);
        else
            await ExecuteCredentialAsync(decision, merchantId, targetId, cancellationToken);
    }

    private async Task ExecuteRoutingAsync(
        ApprovalDecided decision, Guid merchantId, Guid rulesetId, CancellationToken cancellationToken)
    {
        var ruleset = await PlatformReadGuard.ReadAsync(ct => db.RoutingRulesets.Include(x => x.Rules)
            .SingleOrDefaultAsync(x => x.Id == rulesetId && x.MerchantId == merchantId, ct), cancellationToken)
            ?? throw new NotFoundException("Routing ruleset was not found.");
        EnsureApproval(ruleset.ApprovalId, ruleset.Version, decision);

        if (decision.Decision == "rejected")
        {
            ruleset.ReturnToDraft(clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }
        if (decision.Decision != "approved")
            throw new InvalidOperationException("Approval decision is invalid.");

        RoutingRuleset.Validate(ruleset.Rules.Select(x => new RoutingRuleSpec(
            x.Priority, x.Method, x.OriginatorId, x.MinAmount, x.MaxAmount,
            x.TargetConnectionId, x.FallbackConnectionId, x.Enabled)).ToList());

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var active = await PlatformReadGuard.ReadAsync(token => db.RoutingRulesets
                .Where(x => x.MerchantId == merchantId && x.Status == RoutingRulesetStatus.Active && x.Id != ruleset.Id)
                .ToListAsync(token), ct);
            foreach (var prior in active)
                prior.Supersede(clock.UtcNow);
            if (active.Count > 0)
                await unitOfWork.SaveChangesAsync(ct);

            ruleset.Activate(clock.UtcNow);
            EnqueueExecution(decision, true, false, "routing_activated", $"v{ruleset.Version}");
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private async Task ExecuteCredentialAsync(
        ApprovalDecided decision, Guid merchantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.SingleOrDefaultAsync(
            x => x.Id == connectionId && x.MerchantId == merchantId, ct), cancellationToken)
            ?? throw new NotFoundException("PSP connection was not found.");
        EnsureApproval(connection.PendingApprovalId, connection.Version, decision);

        if (decision.Decision == "rejected")
        {
            var rejected = connection.RejectPendingSecretVersion();
            await vault.DiscardVersionAsync(merchantId, rejected, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }
        if (decision.Decision != "approved")
            throw new InvalidOperationException("Approval decision is invalid.");

        var candidateId = connection.PendingSecretVersionId
            ?? throw new InvalidOperationException("PSP credential candidate is missing.");
        var probeSucceeded = false;
        try
        {
            var secret = await vault.ReadVersionForServerAsync(merchantId, candidateId, cancellationToken);
            await adapterFactory.For(connection.Psp).TestConnectionAsync(secret, cancellationToken);
            probeSucceeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            probeSucceeded = false;
        }

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (!probeSucceeded)
            {
                var rejected = connection.RejectPendingSecretVersion();
                await vault.DiscardVersionAsync(merchantId, rejected, ct);
                EnqueueExecution(decision, false, false, "psp_probe_failed", $"v{connection.Version}");
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
            EnqueueExecution(decision, true, false, "psp_credentials_activated", $"v{connection.Version}");
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private void EnqueueExecution(
        ApprovalDecided decision, bool succeeded, bool unknown, string outcome, string? version)
    {
        var message = new ApprovalExecutionReported(
            Guid.CreateVersion7(), decision.ApprovalId, decision.CheckerId,
            succeeded, unknown, outcome, version, decision.CorrelationId, clock.UtcNow);
        db.OutboxMessages.Add(OutboxMessage.Create(
            message.EventId, decision.MerchantId!.Value, ApprovalExecutionReported.EventType,
            ApprovalExecutionReported.SchemaVersion, JsonSerializer.Serialize(message, Json), message.OccurredAt));
    }

    private static void EnsureApproval(Guid? approvalId, long version, ApprovalDecided decision)
    {
        if (approvalId != decision.ApprovalId || decision.TargetVersion != $"v{version}")
            throw new ConcurrencyConflictException("Approval target version changed.");
    }
}
