using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Tests;

/// <summary>
/// The tamper-evidence core: the per-tenant hash chain links each row to the previous one, and the verifier
/// detects a deleted row (Seq gap + broken linkage) or an edited row (hash mismatch). The RLS predicate,
/// INSERT-only grant, bypass proc, and per-tenant applock are SQL-Server-only and covered by the live-SQL
/// integration suite — not here (the SQLite harness has none of them).
/// </summary>
public sealed class VaultRevealAuditTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Mirrors what VaultRevealAuditWriter's direct path does: chain N rows for one tenant.
    private static void Seed(ProducerDbContext db, int count, string name = "psp-secret")
    {
        var prev = VaultRevealAudit.Genesis;
        for (var seq = 1; seq <= count; seq++)
        {
            var row = VaultRevealAudit.Append(prev, Tenant, name, seq, Now.AddMinutes(seq));
            db.VaultRevealAudits.Add(row);
            prev = row.Hash;
        }
        db.SaveChanges();
    }

    [Fact]
    public void ComputeHash_is_deterministic_for_the_same_inputs()
    {
        var a = VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, Tenant, "psp-secret", 1, Now);
        var b = VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, Tenant, "psp-secret", 1, Now);
        var different = VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, Tenant, "psp-secret", 2, Now);

        Assert.Equal(a, b);
        Assert.NotEqual(a, different);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void First_row_links_to_genesis()
    {
        var row = VaultRevealAudit.Append(VaultRevealAudit.Genesis, Tenant, "psp-secret", 1, Now);
        Assert.Equal(VaultRevealAudit.Genesis, row.PrevHash);
        Assert.Equal(VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, Tenant, "psp-secret", 1, Now), row.Hash);
    }

    [Fact]
    public async Task Verifier_passes_on_an_untampered_chain()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        Seed(db, 3);

        var result = await new VaultRevealAuditVerifier(db).VerifyAsync(Tenant, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FirstBrokenSeq);
    }

    [Fact]
    public async Task Verifier_detects_a_deleted_row_as_a_sequence_gap()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using (var seed = harness.NewContext())
            Seed(seed, 3);

        await using (var del = harness.NewContext())
        {
            var middle = await del.VaultRevealAudits.SingleAsync(a => a.Seq == 2);
            del.VaultRevealAudits.Remove(middle);
            await del.SaveChangesAsync();
        }

        await using var db = harness.NewContext();
        var result = await new VaultRevealAuditVerifier(db).VerifyAsync(Tenant, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(3, result.FirstBrokenSeq); // walk reaches Seq 3 where it expected 2
    }

    [Fact]
    public async Task Verifier_detects_an_edited_row_as_a_hash_mismatch()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using (var seed = harness.NewContext())
            Seed(seed, 3);

        await using (var edit = harness.NewContext())
        {
            var row = await edit.VaultRevealAudits.SingleAsync(a => a.Seq == 2);
            // Tamper: rewrite a hashed field without recomputing Hash (EF property bag bypasses private setters).
            edit.Entry(row).Property(nameof(VaultRevealAudit.SecretName)).CurrentValue = "evil-rename";
            await edit.SaveChangesAsync();
        }

        await using var db = harness.NewContext();
        var result = await new VaultRevealAuditVerifier(db).VerifyAsync(Tenant, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(2, result.FirstBrokenSeq);
    }

    [Fact]
    public async Task Audit_row_never_stores_the_secret_value()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        Seed(db, 1, name: "psp-secret"); // the reference NAME, never the secret value

        var row = await db.VaultRevealAudits.SingleAsync();
        Assert.DoesNotContain("sk_live", row.SecretName);
        Assert.Equal("psp-secret", row.SecretName);
    }
}
