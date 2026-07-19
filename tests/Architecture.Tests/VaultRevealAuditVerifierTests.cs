using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Vault;

namespace Architecture.Tests;

/// <summary>
/// The tamper-evidence core (ported from <c>BuildingBlocks.Tests.VaultRevealAuditTests</c> onto
/// <see cref="MerchantRuntimeDbContext"/>, task 8.5.8 — <see cref="VaultRevealAuditVerifier"/> moved into this
/// assembly and is internal, so only a project with InternalsVisibleTo can construct it directly): the
/// per-merchant hash chain links each row to the previous one, and the verifier detects a deleted row (Seq
/// gap + broken linkage) or an edited row (hash mismatch). The chain-APPEND side (genesis, Seq increment,
/// per-merchant independence) is covered separately by <see cref="VaultAuditAppenderTests"/>. The RLS
/// predicate, INSERT-only grant, bypass proc, and per-merchant applock are SQL-Server-only and covered by the
/// live-SQL integration suite — not here (the SQLite harness has none of them).
/// </summary>
public sealed class VaultRevealAuditVerifierTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public VaultRevealAuditVerifierTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    // Unlike VaultAuditAppender, VaultRevealAuditVerifier's own query has no IgnoreQueryFilters() — it relies
    // on the ambient read floor to confine results to the bound actor's own merchant (defense-in-depth: even
    // a wrong merchantId argument could never leak another merchant's chain). So the context here must be
    // BOUND to the target merchant, not unbound.
    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.For(Merchant), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    // Mirrors what VaultAuditAppender's direct path does: chain N rows for one merchant.
    private static void Seed(MerchantRuntimeDbContext db, int count, string name = "psp-secret")
    {
        var prev = VaultRevealAudit.Genesis;
        for (var seq = 1; seq <= count; seq++)
        {
            var row = VaultRevealAudit.Append(prev, Merchant, name, seq, Now.AddMinutes(seq));
            db.VaultRevealAudits.Add(row);
            prev = row.Hash;
        }
        db.SaveChanges();
    }

    [Fact]
    public void ComputeHash_is_deterministic_for_the_same_inputs()
    {
        var a = VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, Merchant, "psp-secret", 1, Now);
        var b = VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, Merchant, "psp-secret", 1, Now);
        var different = VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, Merchant, "psp-secret", 2, Now);

        Assert.Equal(a, b);
        Assert.NotEqual(a, different);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void First_row_links_to_genesis()
    {
        var row = VaultRevealAudit.Append(VaultRevealAudit.Genesis, Merchant, "psp-secret", 1, Now);
        Assert.Equal(VaultRevealAudit.Genesis, row.PrevHash);
        Assert.Equal(VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, Merchant, "psp-secret", 1, Now), row.Hash);
    }

    [Fact]
    public async Task Verifier_passes_on_an_untampered_chain()
    {
        using var db = NewContext();
        Seed(db, 3);

        var result = await new VaultRevealAuditVerifier(db).VerifyAsync(Merchant, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FirstBrokenSeq);
    }

    [Fact]
    public async Task Verifier_detects_a_deleted_row_as_a_sequence_gap()
    {
        using (var seed = NewContext())
            Seed(seed, 3);

        // Simulate out-of-band tampering (the scenario the verifier exists to catch) via raw SQL — the
        // append-only write guard (task 3) unconditionally rejects a tracked Delete/Modified on this entity
        // through the ordinary EF path, by design; a real attacker with direct DB access bypasses the app
        // (and its guard) the same way.
        using (var del = NewContext())
            await del.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM VaultRevealAudits WHERE MerchantId = {Merchant} AND Seq = {2}");

        using var db = NewContext();
        var result = await new VaultRevealAuditVerifier(db).VerifyAsync(Merchant, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(3, result.FirstBrokenSeq); // walk reaches Seq 3 where it expected 2
    }

    [Fact]
    public async Task Verifier_detects_an_edited_row_as_a_hash_mismatch()
    {
        using (var seed = NewContext())
            Seed(seed, 3);

        // Simulate out-of-band tampering via raw SQL — see the Delete test's comment for why this cannot go
        // through the ordinary tracked-entity EF path.
        using (var edit = NewContext())
            await edit.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE VaultRevealAudits SET SecretName = {"evil-rename"} WHERE MerchantId = {Merchant} AND Seq = {2}");

        using var db = NewContext();
        var result = await new VaultRevealAuditVerifier(db).VerifyAsync(Merchant, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(2, result.FirstBrokenSeq);
    }

    [Fact]
    public async Task Audit_row_never_stores_the_secret_value()
    {
        using var db = NewContext();
        Seed(db, 1, name: "psp-secret"); // the reference NAME, never the secret value

        var row = await db.VaultRevealAudits.IgnoreQueryFilters().SingleAsync();
        Assert.DoesNotContain("sk_live", row.SecretName);
        Assert.Equal("psp-secret", row.SecretName);
    }

    public void Dispose() => _connection.Dispose();
}
