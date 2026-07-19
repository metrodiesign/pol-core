using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Vault;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter task 6 vault-audit append port (design.md transaction inventory row 22;
/// REQ-7/REQ-1.6): genesis on a merchant's first reveal, the hash chain links to the correct prior head,
/// <c>Seq</c> increments per merchant, chains stay independent across merchants, and — like the other task 5
/// suppressed-op ports — <c>IgnoreQueryFilters()</c> genuinely matters: an unbound actor's ordinary filtered
/// query (<c>CurrentMerchant == Guid.Empty</c>) never finds an existing chain head for a real merchant. SQLite
/// has no <c>sp_getapplock</c>, so this exercises the no-lock fallback branch only — the real SQL Server
/// applock mechanism is proven separately by an Integration.Tests raw-SQL test against real SQL Server.
/// </summary>
public sealed class VaultAuditAppenderTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection;

    public VaultAuditAppenderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private async Task<VaultRevealAudit> LoadOnlyRowAsync(Guid merchantId)
    {
        using var reader = NewContext();
        return await reader.VaultRevealAudits.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.MerchantId == merchantId).OrderByDescending(a => a.Seq).FirstAsync();
    }

    [Fact]
    public async Task First_append_for_a_merchant_chains_from_genesis_at_Seq_1()
    {
        var now = DateTime.UtcNow;
        using (var db = NewContext())
            await new VaultAuditAppender(db, NoOpSecurityTelemetry.Instance).AppendAsync(MerchantA, "psp-secret", now, CancellationToken.None);

        var row = await LoadOnlyRowAsync(MerchantA);
        Assert.Equal(1L, row.Seq);
        Assert.Equal(VaultRevealAudit.Genesis, row.PrevHash);
        Assert.Equal(VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, MerchantA, "psp-secret", 1, now), row.Hash);
    }

    [Fact]
    public async Task Second_append_for_the_same_merchant_chains_from_the_prior_head_at_Seq_2()
    {
        var t1 = DateTime.UtcNow;
        var t2 = t1.AddSeconds(1);
        using (var first = NewContext())
            await new VaultAuditAppender(first, NoOpSecurityTelemetry.Instance).AppendAsync(MerchantB, "one", t1, CancellationToken.None);
        var firstRow = await LoadOnlyRowAsync(MerchantB);

        using (var second = NewContext())
            await new VaultAuditAppender(second, NoOpSecurityTelemetry.Instance).AppendAsync(MerchantB, "two", t2, CancellationToken.None);

        var row = await LoadOnlyRowAsync(MerchantB);
        Assert.Equal(2L, row.Seq);
        Assert.Equal(firstRow.Hash, row.PrevHash);
    }

    [Fact]
    public async Task Chains_for_different_merchants_stay_independent()
    {
        var now = DateTime.UtcNow;
        using (var a1 = NewContext())
            await new VaultAuditAppender(a1, NoOpSecurityTelemetry.Instance).AppendAsync(MerchantA, "one", now, CancellationToken.None);
        using (var a2 = NewContext())
            await new VaultAuditAppender(a2, NoOpSecurityTelemetry.Instance).AppendAsync(MerchantA, "two", now, CancellationToken.None);
        using (var b1 = NewContext())
            await new VaultAuditAppender(b1, NoOpSecurityTelemetry.Instance).AppendAsync(MerchantB, "one", now, CancellationToken.None);

        var lastA = await LoadOnlyRowAsync(MerchantA);
        var lastB = await LoadOnlyRowAsync(MerchantB);
        Assert.Equal(2L, lastA.Seq);
        Assert.Equal(1L, lastB.Seq); // MerchantA's appends never bumped MerchantB's chain
        Assert.Equal(VaultRevealAudit.Genesis, lastB.PrevHash);
    }

    [Fact]
    public async Task Append_finds_an_existing_head_even_with_no_actor_bound()
    {
        // The mechanism this port exists for: an unbound context's ordinary filtered query (CurrentMerchant ==
        // Guid.Empty) never matches a real merchant's rows, so without IgnoreQueryFilters the SECOND append
        // would wrongly restart the chain at Seq=1 instead of continuing from the existing head.
        var now = DateTime.UtcNow;
        using (var first = NewContext())
            await new VaultAuditAppender(first, NoOpSecurityTelemetry.Instance).AppendAsync(MerchantA, "one", now, CancellationToken.None);

        using var filtered = NewContext();
        Assert.Equal(0, await filtered.VaultRevealAudits.CountAsync()); // filter hides it from an unbound actor

        using var second = NewContext();
        await new VaultAuditAppender(second, NoOpSecurityTelemetry.Instance).AppendAsync(MerchantA, "two", now, CancellationToken.None);

        var row = await LoadOnlyRowAsync(MerchantA);
        Assert.Equal(2L, row.Seq); // continued the chain, did not restart it
    }

    public void Dispose() => _connection.Dispose();
}
