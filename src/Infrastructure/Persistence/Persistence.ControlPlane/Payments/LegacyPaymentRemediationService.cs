using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using ControlPlanePaymentAuthorizationSqlLockManager = Persistence.ControlPlane.Payments.PaymentAuthorizationSqlLockManager;
using SharedKernel;
using BuildingBlocks.Infrastructure.Persistence;

namespace Persistence.ControlPlane.Payments;

/// <summary>
/// The concrete <see cref="ILegacyPaymentRemediation"/> for the merchant-psp-settings cutover (design
/// "Migration and cutover" steps 3, 5-9). Offline/operator only — no HTTP route, never at boot. The charge
/// proof is READ-ONLY against the PSP: it fetches a charge with each historical secret but never confirms or
/// marks a session paid (that would be <see cref="PaymentConfirmationService"/>'s job), so the only durable
/// write is the version 0 -> version 1 snapshot upgrade under the merchant exclusive lock.
/// </summary>
internal sealed class LegacyPaymentRemediationService(
    ControlPlaneDbContext db,
    CommerceDbContext commerceDb,
    [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
    ControlPlanePaymentAuthorizationSqlLockManager locks,
    IVaultSecretStore vault,
    IPspAdapterFactory adapters,
    IPaymentRouteSelector routeSelector,
    IClock clock) : ILegacyPaymentRemediation
{
    public Task<int> BackfillMerchantEnvironmentsAsync(
        Guid actorId, PspEnvironment globalDefault, CancellationToken cancellationToken)
    {
        RequireActor(actorId);
        return unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireGlobalExclusiveAsync(ct);
            // Only merchants that have never switched environment (the expand migration seeded
            // PaymentEnvironmentUpdatedAt = CreatedAt; a real switch bumps it), and only when the value would
            // actually change — so a re-run is a no-op and merchants created after cutover keep the sandbox
            // default. No blind touch of PaymentEnvironmentUpdatedAt: a backfill is not a switch.
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [merch].[Merchants]
                SET [PaymentEnvironment] = {(int)globalDefault}, [Version] = [Version] + 1
                WHERE [PaymentEnvironmentUpdatedAt] = [CreatedAt]
                  AND [PaymentEnvironment] <> {(int)globalDefault}
                  AND [PendingPaymentEnvironmentApprovalId] IS NULL;
                """, ct);
        }, cancellationToken);
    }

    public async Task<LegacySessionRemediationReport> RemediateSessionsAsync(
        Guid actorId, PspEnvironment legacyEnvironment, CancellationToken cancellationToken)
    {
        RequireActor(actorId);
        var merchantIds = await PlatformReadGuard.ReadAsync(ct => commerceDb.PaymentSessions
            .IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.RoutingSnapshotVersion == 0)
            .Select(s => s.MerchantId).Distinct().ToListAsync(ct), cancellationToken);

        var upgraded = 0;
        var blocked = 0;
        foreach (var merchantId in merchantIds)
        {
            var (u, b) = await RemediateMerchantAsync(merchantId, legacyEnvironment, cancellationToken);
            upgraded += u;
            blocked += b;
        }
        return new LegacySessionRemediationReport(upgraded, blocked);
    }

    private async Task<(int Upgraded, int Blocked)> RemediateMerchantAsync(
        Guid merchantId, PspEnvironment legacyEnvironment, CancellationToken cancellationToken)
    {
        // Read-only phase (no transaction, no lock held across the PSP fetch): decide each legacy row's target
        // secret version. A no-charge row is re-routed with the current selector; a charged row proves ONE
        // historical secret by fetch-to-confirm.
        var sessions = await PlatformReadGuard.ReadAsync(ct => commerceDb.PaymentSessions
            .IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.MerchantId == merchantId && s.RoutingSnapshotVersion == 0)
            .ToListAsync(ct), cancellationToken);

        var decisions = new List<(Guid SessionId, Guid ConnectionId, Guid SecretVersionId, PspEnvironment Environment)>();
        var blocked = 0;
        foreach (var session in sessions)
        {
            var decision = session.PspExternalChargeId is null
                ? await DecideUnchargedAsync(session, cancellationToken)
                : await DecideChargedAsync(session, legacyEnvironment, cancellationToken);
            if (decision is { } d)
                decisions.Add((session.Id, d.ConnectionId, d.SecretVersionId, d.Environment));
            else
                blocked++;
        }

        if (decisions.Count == 0)
            return (0, blocked);

        // Write phase: a single short transaction under the merchant exclusive lock (the same lock an
        // environment activation takes), reloading each decided row tracked and upgrading it only while it is
        // still a legacy snapshot.
        var upgraded = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireMerchantExclusiveAsync(merchantId, ct);
            var count = 0;
            var now = clock.UtcNow;
            foreach (var decision in decisions)
            {
                var session = await commerceDb.PaymentSessions.IgnoreQueryFilters()
                    .SingleOrDefaultAsync(s => s.Id == decision.SessionId, ct);
                if (session is null || session.RoutingSnapshotVersion != 0)
                    continue;
                session.UpgradeLegacySnapshot(
                    decision.ConnectionId, decision.SecretVersionId, decision.Environment, now);
                count++;
            }
            await commerceDb.SaveChangesAsync(ct);
            await unitOfWork.SaveChangesAsync(ct);
            return count;
        }, cancellationToken);

        return (upgraded, blocked);
    }

    private async Task<(Guid ConnectionId, Guid SecretVersionId, PspEnvironment Environment)?> DecideUnchargedAsync(
        Session session, CancellationToken cancellationToken)
    {
        // No external charge exists, so there is no historical secret to prove: re-route with the current
        // selector and pin what it chooses (design step 5). An uncovered method leaves the row version 0.
        try
        {
            var route = await routeSelector.SelectAsync(
                session.MerchantId, session.OrderId, session.Method, cancellationToken);
            return (route.PspConnectionId, route.SecretVersionId, route.Environment);
        }
        catch (ConflictException)
        {
            return null;
        }
    }

    private async Task<(Guid ConnectionId, Guid SecretVersionId, PspEnvironment Environment)?> DecideChargedAsync(
        Session session, PspEnvironment legacyEnvironment, CancellationToken cancellationToken)
    {
        var connectionId = await PlatformReadGuard.ReadAsync(ct => db.PspConnections
            .IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.MerchantId == session.MerchantId && c.Psp == session.Psp)
            .Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct), cancellationToken);
        if (connectionId is not { } connId)
            return null;

        var secretName = $"psp-connection-{connId:N}";
        var versionIds = await PlatformReadGuard.ReadAsync(ct => db.VaultSecretVersions
            .IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.MerchantId == session.MerchantId && v.SecretName == secretName
                && v.State != VaultSecretVersionState.Discarded)
            .OrderBy(v => v.Version)
            .Select(v => v.Id).ToListAsync(ct), cancellationToken);

        var adapter = adapters.For(session.Psp);
        Guid? proven = null;
        foreach (var versionId in versionIds)
        {
            if (!await ChargeConfirmsAsync(adapter, session, versionId, legacyEnvironment, cancellationToken))
                continue;
            if (proven is not null)
                return null; // Two historical secrets both confirm — ambiguous, keep version 0 (design step 9).
            proven = versionId;
        }

        return proven is { } id ? (connId, id, legacyEnvironment) : null;
    }

    private async Task<bool> ChargeConfirmsAsync(
        IPspAdapter adapter, Session session, Guid versionId, PspEnvironment legacyEnvironment,
        CancellationToken cancellationToken)
    {
        // Any failure with a candidate secret — the vault refusing to reveal it (discarded/expired-staged/
        // key gone) or the PSP rejecting the fetch (wrong account -> 401, ambiguous, timeout) — means this
        // secret does not prove the charge. Skip it; never let one bad version abort the whole run.
        string secret;
        try
        {
            secret = await vault.ReadVersionForServerAsync(session.MerchantId, versionId, cancellationToken);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }

        try
        {
            var confirmation = await adapter.FetchChargeAsync(
                session.PspExternalChargeId!, secret, legacyEnvironment, cancellationToken);
            // A null amount means the PSP did not report one (PspChargeConfirmation contract) — confirm on the
            // fact that this secret could read the charge, never disqualify. A reported amount must match the
            // session (Money equality covers the currency).
            return confirmation.Amount is null || confirmation.Amount == session.Amount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static void RequireActor(Guid actorId)
    {
        if (actorId == Guid.Empty)
            throw new ArgumentException("ActorId is required.", nameof(actorId));
    }
}
