using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Persists each reveal-audit row on a FRESH, merchant-bound DI scope — never the caller's DbContext. Two
/// reasons: (1) EF SaveChanges is all-or-nothing over the change tracker, so writing on the caller's context
/// would commit its half-built work; (2) the audit must survive a caller rollback. The fresh scope binds the
/// merchant via <see cref="AmbientActor"/> so its connection opens with SESSION_CONTEXT('MerchantId') set,
/// satisfying the audit table's BLOCK predicate. On SQL Server the per-merchant chain head is read through the
/// bypass proc under a transaction-owned applock (pol_app has no SELECT on the table); the unit-test SQLite
/// path reads the head directly (no RLS/proc there).
/// </summary>
public sealed class VaultRevealAuditWriter : IVaultRevealAuditWriter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;

    public VaultRevealAuditWriter(IServiceScopeFactory scopeFactory, IClock clock)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
    }

    public async Task AppendAsync(Guid merchantId, string secretName, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var ambient = scope.ServiceProvider.GetRequiredService<AmbientActor>();
        using var binding = ambient.Begin(merchantId);
        var db = scope.ServiceProvider.GetRequiredService<PolDbContext>();

        if (db.Database.IsSqlServer())
            await AppendViaProcAsync(db, merchantId, secretName, cancellationToken).ConfigureAwait(false);
        else
            await AppendDirectAsync(db, merchantId, secretName, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendViaProcAsync(PolDbContext db, Guid merchantId, string secretName, CancellationToken ct)
    {
        // One transaction so usp_vault_audit_head's transaction-owned applock holds across the head read and
        // the insert -> concurrent reveals for the same merchant serialize and cannot fork the chain.
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var head = await db.Database
            .SqlQueryRaw<VaultAuditHead>("EXEC sec.usp_vault_audit_head @MerchantId = {0}", merchantId)
            .ToListAsync(ct).ConfigureAwait(false);

        Append(db, head.Count > 0 ? head[0].LastHash : VaultRevealAudit.Genesis,
            head.Count > 0 ? head[0].LastSeq + 1 : 1L, merchantId, secretName);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task AppendDirectAsync(PolDbContext db, Guid merchantId, string secretName, CancellationToken ct)
    {
        var head = await db.VaultRevealAudits
            .Where(a => a.MerchantId == merchantId)
            .OrderByDescending(a => a.Seq)
            .Select(a => new { a.Seq, a.Hash })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        Append(db, head?.Hash ?? VaultRevealAudit.Genesis, (head?.Seq ?? 0) + 1, merchantId, secretName);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void Append(PolDbContext db, byte[] prevHash, long seq, Guid merchantId, string secretName) =>
        db.VaultRevealAudits.Add(VaultRevealAudit.Append(prevHash, merchantId, secretName, seq, _clock.UtcNow));
}

/// <summary>Row shape returned by usp_vault_audit_head (column aliases LastSeq / LastHash).</summary>
internal sealed class VaultAuditHead
{
    public long LastSeq { get; set; }
    public byte[] LastHash { get; set; } = default!;
}
