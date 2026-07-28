using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantRuntime.Vault;

/// <summary>
/// rls-to-query-filter task 6 (design.md transaction inventory row 22; REQ-7): the narrow per-operation port
/// that replaces <c>sec.usp_vault_audit_head</c> — the SAME <c>sp_getapplock</c>(Exclusive, transaction-owned)
/// + head-read + append the proc did under <c>EXECUTE AS 'pol_vault_auditor'</c>, now issued directly by
/// application code inside ONE <see cref="MerchantRuntimeDbContext"/> transaction (not
/// <c>ExecuteInTransactionAsync</c> — this owns its own <c>BeginTransactionAsync</c>, matching the proc's own
/// transaction-owned lock scope exactly).
/// <para>
/// The head read takes an EXPLICIT <paramref name="merchantId"/> parameter under <c>IgnoreQueryFilters()</c>
/// rather than trusting whatever actor the injected context happens to be bound to (REQ-1.6's "narrow
/// per-operation port" — a mismatched ambient actor must never silently read/append the wrong merchant's
/// chain); the unique <c>(MerchantId, Seq)</c> index stays the backstop if the lock is ever bypassed (REQ-7.3).
/// </para>
/// <para>
/// SQLite has no <c>sp_getapplock</c> — the unit-test harness reads the head directly with no lock (mirrors
/// the CURRENT <c>VaultRevealAuditWriter.AppendDirectAsync</c> fallback exactly); only the SQL Server branch
/// is the real REQ-7.2 mechanism. Scaffolding only ("Build the ports, defer the DI flip", carried from task
/// 4/5): <c>pol_app</c> does not yet have SELECT on <c>merch.VaultRevealAudits</c> (that grant, plus wiring
/// this port as <c>IVaultRevealAuditWriter</c>'s live implementation, is task 8's "1 principal" collapse,
/// owner-confirmed at design.md's sign-off gate) — the CURRENT proc-based writer keeps running production
/// traffic unchanged; this port is proven standalone via SQLite (Architecture.Tests) and a raw-SQL integration
/// test that exercises the identical <c>sp_getapplock</c> statements against real SQL Server
/// (Integration.Tests, which has no InternalsVisibleTo into this assembly).
/// </para>
/// </summary>
internal interface IVaultAuditAppender
{
    Task AppendAsync(Guid merchantId, string secretName, DateTime revealedAt, CancellationToken cancellationToken);
}

internal sealed class VaultAuditAppender(MerchantRuntimeDbContext db, ISecurityTelemetry telemetry) : IVaultAuditAppender
{
    public async Task AppendAsync(Guid merchantId, string secretName, DateTime revealedAt, CancellationToken cancellationToken)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        if (db.Database.IsSqlServer())
            await AcquireChainLockAsync(merchantId, cancellationToken);

        var head = await db.VaultRevealAudits.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.MerchantId == merchantId)
            .OrderByDescending(a => a.Seq)
            .Select(a => new { a.Seq, a.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        db.VaultRevealAudits.Add(VaultRevealAudit.Append(
            head?.Hash ?? VaultRevealAudit.Genesis, merchantId, secretName, (head?.Seq ?? 0) + 1, revealedAt));

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // Mirrors sec.usp_vault_audit_head's own EXEC @lock = sp_getapplock(...) call exactly (same resource-name
    // shape, same Exclusive/Transaction/15s-timeout arguments) so the two are interchangeable serialization-wise.
    private async Task AcquireChainLockAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        // ToListAsync, never SingleAsync: Single/First COMPOSE a TOP(2) wrapper around the raw SQL, and a
        // multi-statement DECLARE/EXEC batch is not composable — EF throws "'FromSql' or 'SqlQuery' was called
        // with non-composable SQL" while GENERATING the query, so the lock is never even attempted and every
        // vault reveal fails on SQL Server. The integration suite cannot catch it: this port is internal, so
        // VaultAuditAppenderIntegrationTests drives the identical SQL through raw ADO instead of through EF.
        // Same materialization ProvisioningCoordinator already uses for its own raw probe.
        var lockResults = await db.Database.SqlQueryRaw<int>(
            """
            DECLARE @lock int;
            EXEC @lock = sp_getapplock @Resource = {0}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            SELECT @lock AS Value;
            """, LockResource(merchantId)).ToListAsync(cancellationToken);
        var lockResult = lockResults.Single();

        if (lockResult < 0)
        {
            telemetry.Emit(new DenialEvent(
                DenialCategory.ApplockTimeout, "system", ActorId: null, merchantId,
                nameof(VaultRevealAudit), "Append", "sp_getapplock did not acquire the vault audit chain lock in time.",
                CorrelationId.Current, DateTime.UtcNow));
            throw new InvalidOperationException("Could not acquire the vault audit chain lock for the merchant.");
        }
    }

    private static string LockResource(Guid merchantId) => $"vault-audit:{merchantId}";
}
