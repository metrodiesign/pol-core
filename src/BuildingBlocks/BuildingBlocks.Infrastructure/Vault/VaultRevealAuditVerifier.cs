using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Re-walks a merchant's chain in Seq order and recomputes each row's hash: a deleted row breaks Seq
/// contiguity, an edited row breaks its own hash, and either breaks the PrevHash linkage of every later row.
/// The first failing Seq is reported. Read-only; never touches any secret (the table holds none).
/// </summary>
public sealed class VaultRevealAuditVerifier : IVaultRevealAuditVerifier
{
    private readonly PolDbContext _db;

    public VaultRevealAuditVerifier(PolDbContext db) => _db = db;

    public async Task<VaultAuditVerifyResult> VerifyAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        var rows = await _db.VaultRevealAudits
            .Where(a => a.MerchantId == merchantId)
            .OrderBy(a => a.Seq)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        byte[] expectedPrev = VaultRevealAudit.Genesis;
        long expectedSeq = 1;

        foreach (var row in rows)
        {
            if (row.Seq != expectedSeq)
                return new VaultAuditVerifyResult(merchantId, false, row.Seq,
                    $"Sequence gap: expected {expectedSeq}, found {row.Seq} (a row was deleted).");

            if (!row.PrevHash.AsSpan().SequenceEqual(expectedPrev))
                return new VaultAuditVerifyResult(merchantId, false, row.Seq,
                    "PrevHash does not match the prior row's hash (chain broken).");

            var recomputed = VaultRevealAudit.ComputeHash(row.PrevHash, merchantId, row.SecretName, row.Seq, row.RevealedAt);
            if (!row.Hash.AsSpan().SequenceEqual(recomputed))
                return new VaultAuditVerifyResult(merchantId, false, row.Seq,
                    "Row hash does not match its contents (row edited).");

            expectedPrev = row.Hash;
            expectedSeq++;
        }

        return new VaultAuditVerifyResult(merchantId, true, null, null);
    }
}
