using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantUsers;
using Products.Domain;
using SharedKernel;
using MerchantUserAccount = Merchants.Domain.Users.User;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter read floor (REQ-1, REQ-3.1, REQ-11.6, REQ-11.7) end to end on
/// <see cref="MerchantRuntimeDbContext"/> and <see cref="MerchantUserDbContext"/>: cross-merchant reads
/// return zero rows, a by-id load of another merchant's row is IDOR-closed (null, not an exception), an
/// unbound actor sees nothing, and the filter genuinely re-evaluates per DbContext INSTANCE (not baked into
/// the shared/cached compiled model) — proven both by differing query RESULTS and by inspecting the
/// generated SQL's parameter placeholder.
/// </summary>
public sealed class ReadFloorTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection;

    public ReadFloorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewMerchantRuntimeContext(FakeActorContext.Unbound);
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewMerchantRuntimeContext(IActorContext actor) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options, actor,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task Merchant_A_never_reads_merchant_B_products()
    {
        await SeedProductAsync(MerchantA, "a-product");
        await SeedProductAsync(MerchantB, "b-product");

        using var asA = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA));
        var visible = await asA.Products.ToListAsync();

        Assert.Single(visible);
        Assert.Equal("a-product", visible[0].DocumentNo);
    }

    [Fact]
    public async Task By_id_load_of_another_merchants_row_is_IDOR_closed()
    {
        var otherMerchantProductId = await SeedProductAsync(MerchantB, "b-product");

        using var asA = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA));
        var found = await asA.Products.FirstOrDefaultAsync(p => p.Id == otherMerchantProductId);

        Assert.Null(found);
    }

    [Fact]
    public async Task Unbound_actor_reads_zero_rows()
    {
        await SeedProductAsync(MerchantA, "a-product");
        await SeedProductAsync(MerchantB, "b-product");

        using var unbound = NewMerchantRuntimeContext(FakeActorContext.Unbound);
        var visible = await unbound.Products.ToListAsync();

        Assert.Empty(visible);
    }

    [Fact]
    public async Task Two_context_instances_with_different_actors_see_different_rows_from_the_shared_model()
    {
        // REQ-1.5 / REQ-11.6: build with the SAME DbContextOptions<T> shape (default service-provider
        // caching, unlike the anti-flake Sqlite tests elsewhere) so the compiled MODEL can be shared/cached
        // across instances — proving the filter is an INSTANCE member re-evaluated per query, not a value
        // baked into that shared cached model at first build.
        await SeedProductAsync(MerchantA, "a-product");
        await SeedProductAsync(MerchantB, "b-product");

        using var asA = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA));
        using var asB = NewMerchantRuntimeContext(FakeActorContext.For(MerchantB));

        var seenByA = await asA.Products.Select(p => p.DocumentNo).ToListAsync();
        var seenByB = await asB.Products.Select(p => p.DocumentNo).ToListAsync();

        Assert.Equal(["a-product"], seenByA);
        Assert.Equal(["b-product"], seenByB);
    }

    [Fact]
    public void Generated_SQL_carries_a_parameter_placeholder_not_a_baked_literal_GUID()
    {
        // EF's ToQueryString() prints a "-- @paramName='value'" debug header ABOVE the statement — that
        // header legitimately shows the current value; the proof of REQ-1.5 is that the WHERE clause itself
        // references the parameter NAME (which embeds the captured "context.CurrentMerchant" instance
        // member, per EF's parameter-naming convention), not the raw GUID inlined as a literal.
        using var asA = NewMerchantRuntimeContext(FakeActorContext.For(MerchantA));
        var sql = asA.Products.ToQueryString();
        var whereLine = sql.Split('\n').Single(l => l.Contains("WHERE", StringComparison.Ordinal));

        // EF names a query-filter-captured instance member's parameter "@ef_filter__<MemberName>" — the name
        // itself confirms the filter binds to CurrentMerchant, the instance member, not a baked constant.
        Assert.Contains("@ef_filter__CurrentMerchant", sql);
        Assert.Contains("@ef_filter__CurrentMerchant", whereLine);
        Assert.DoesNotContain(MerchantA.ToString(), whereLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_merch_user_MerchantId_null_is_invisible_under_the_merchant_filter()
    {
        // REQ-11.7 carve-out: a pending registration (MerchantId NULL) must not leak to ANY merchant actor —
        // NULL never equals a bound Guid in SQL, so the ordinary filter closes this without special-casing.
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        MerchantUserDbContext NewContext(IActorContext actor) =>
            new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options, actor,
                FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

        using (var setup = NewContext(FakeActorContext.Unbound))
            await setup.Database.EnsureCreatedAsync();

        using (var write = NewContext(FakeActorContext.Unbound))
        {
            write.Add(MerchantUserAccount.Register("pending-subject", "pending@example.com", DateTime.UtcNow));
            await write.SaveChangesAsync();
        }

        using var asA = NewContext(FakeActorContext.For(MerchantA));
        var visible = await asA.Users.ToListAsync();

        Assert.Empty(visible);
        connection.Dispose();
    }

    private async Task<Guid> SeedProductAsync(Guid merchantId, string documentNo)
    {
        using var writer = NewMerchantRuntimeContext(FakeActorContext.For(merchantId));
        var product = Product.Create(
            new ProductInput(
                merchantId, ProductGroup.VMI, DocumentType.POLICY, documentNo, "100", "00098",
                Money.Of(10m, "THB")),
            DateTime.UtcNow);
        writer.Add(product);
        await writer.SaveChangesAsync();
        return product.Id;
    }

    public void Dispose() => _connection.Dispose();
}
