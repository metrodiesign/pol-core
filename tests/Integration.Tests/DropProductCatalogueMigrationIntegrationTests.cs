using BuildingBlocks.Infrastructure.Persistence.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Integration.Tests;

/// <summary>
/// products-external-source-of-truth REQ-6.2/6.3/6.8 — the catalogue cutover migration
/// (<c>DropProductCatalogueCutoverDocumentNo</c>) run for real. Two claims here no unit test can make: the
/// hand-written <c>Up()</c> backfills every cart line's <c>DocumentNo</c> from <c>shop.Products</c> BEFORE that
/// table is dropped (REQ-6.2) and deletes the lines that no longer resolve to a product rather than leaving them
/// blank (REQ-6.3); and <c>Down()</c> restores the STRUCTURE (the <c>ProductId</c> columns and an empty
/// <c>shop.Products</c>) without the data (REQ-6.8).
/// <para>The migration under test is the PRODUCTION type — its operations are rendered by EF's real SQL Server
/// generator and executed against a real SQL Server, on a scratch database holding the buy-path tables in their
/// pre-migration shape with data in them. Rendering (rather than hand-writing the expected SQL) is the point:
/// whatever the migration says is what runs.</para>
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DropProductCatalogueMigrationIntegrationTests
{
    private static readonly Guid MatchedProductId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private const string DocumentNo = "69100/กธ/900123";
    private const string SaleCode = "00098";
    private const string ProductGroup = "VMI";

    [Fact]
    public async Task The_cutover_backfills_each_cart_line_and_drops_the_orphans()
    {
        var database = $"pol_dropproduct_backfill_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var c = await OpenScratchAsync(database);
            await CreatePreMigrationTablesAsync(c);

            // A product still in the mirror, plus two cart lines: one pointing at it (must be backfilled) and one
            // pointing at a product that no longer exists (must be deleted, not left with NULLs — REQ-6.3).
            await IntegrationDb.ExecAsync(c,
                "INSERT shop.Products (Id, DocumentNo, SaleCode, ProductGroup) VALUES (@id, @doc, @sale, @grp);",
                ("@id", MatchedProductId), ("@doc", DocumentNo), ("@sale", SaleCode), ("@grp", ProductGroup));

            var matched = Guid.NewGuid();
            var orphan = Guid.NewGuid();
            await IntegrationDb.ExecAsync(c,
                "INSERT shop.CartItems (Id, ProductId) VALUES (@id, @pid);",
                ("@id", matched), ("@pid", MatchedProductId));
            await IntegrationDb.ExecAsync(c,
                "INSERT shop.CartItems (Id, ProductId) VALUES (@id, @pid);",
                ("@id", orphan), ("@pid", Guid.NewGuid()));

            foreach (var sql in RenderScript(m => m.UpOperations))
                await IntegrationDb.ExecAsync(c, sql);

            // REQ-6.2 — the matched line carries the product's own values now.
            Assert.Equal(DocumentNo, (string?)await ColumnAsync(c, matched, "DocumentNo"));
            Assert.Equal(SaleCode, (string?)await ColumnAsync(c, matched, "SaleCode"));
            Assert.Equal(ProductGroup, (string?)await ColumnAsync(c, matched, "ProductGroup"));

            // REQ-6.3 — the orphan is gone, not left blank.
            Assert.Equal(0, (int)(await IntegrationDb.ScalarAsync(c,
                "SELECT COUNT(*) FROM shop.CartItems WHERE Id = @id;", ("@id", orphan)))!);

            // REQ-6.2 — the three columns are NOT NULL after the backfill.
            foreach (var column in (string[])["DocumentNo", "SaleCode", "ProductGroup"])
                Assert.False(await IsNullableAsync(c, "CartItems", column));

            // REQ-6.1 — the mirror is gone and the surrogate identifier with it. OBJECT_ID of a missing object
            // is SQL NULL, which comes back as DBNull (not a C# null).
            Assert.IsType<DBNull>(await IntegrationDb.ScalarAsync(c, "SELECT OBJECT_ID(N'shop.Products');"));
            Assert.False(await ColumnExistsAsync(c, "CartItems", "ProductId"));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Down_restores_the_structure_without_the_data()
    {
        var database = $"pol_dropproduct_down_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var c = await OpenScratchAsync(database);
            await CreatePreMigrationTablesAsync(c);

            foreach (var sql in RenderScript(m => m.UpOperations))
                await IntegrationDb.ExecAsync(c, sql);
            foreach (var sql in RenderScript(m => m.DownOperations))
                await IntegrationDb.ExecAsync(c, sql);

            // REQ-6.8 — the structure comes back: the ProductId columns (NOT NULL, as they were) on all three
            // buy-path tables, and an empty shop.Products.
            foreach (var table in (string[])["CartItems", "CheckoutSessionItems", "OrderItems"])
            {
                Assert.True(await ColumnExistsAsync(c, table, "ProductId"));
                Assert.False(await IsNullableAsync(c, table, "ProductId"));
            }

            Assert.IsNotType<DBNull>(await IntegrationDb.ScalarAsync(c, "SELECT OBJECT_ID(N'shop.Products');"));
            Assert.Equal(0, (int)(await IntegrationDb.ScalarAsync(c, "SELECT COUNT(*) FROM shop.Products;"))!);

            // …and the cutover columns are gone again.
            Assert.False(await ColumnExistsAsync(c, "CartItems", "DocumentNo"));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    // ---- harness -------------------------------------------------------------------------------------

    private static Task<object?> ColumnAsync(SqlConnection c, Guid id, string column) =>
        IntegrationDb.ScalarAsync(c, $"SELECT {column} FROM shop.CartItems WHERE Id = @id;", ("@id", id));

    private static async Task<bool> IsNullableAsync(SqlConnection c, string table, string column) =>
        (bool)(await IntegrationDb.ScalarAsync(c,
            """
            SELECT c.is_nullable FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(N'shop.' + @table) AND c.name = @column;
            """,
            ("@table", table), ("@column", column)))!;

    private static async Task<bool> ColumnExistsAsync(SqlConnection c, string table, string column) =>
        await IntegrationDb.ScalarAsync(c,
            """
            SELECT 1 FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(N'shop.' + @table) AND c.name = @column;
            """,
            ("@table", table), ("@column", column)) is not null;

    /// <summary>Renders the production migration's operations through EF's real SQL Server generator.</summary>
    private static IReadOnlyList<string> RenderScript(
        Func<Migration, IReadOnlyList<MigrationOperation>> pick)
    {
        var migration = (Migration)new DropProductCatalogueCutoverDocumentNo();
        using var context = new ScratchContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        return [.. generator.Generate(pick(migration)).Select(command => command.CommandText)];
    }

    /// <summary>The buy-path tables as they stand BEFORE this migration: each carries the surrogate
    /// <c>ProductId</c>, and <c>shop.Products</c> holds the columns the backfill reads. Only what the migration
    /// touches is modelled — a scratch database keeps the suite's shared VCentralPay untouched.</summary>
    private static async Task CreatePreMigrationTablesAsync(SqlConnection c)
    {
        await IntegrationDb.ExecAsync(c, "EXEC('CREATE SCHEMA shop');");
        await IntegrationDb.ExecAsync(c,
            """
            CREATE TABLE shop.Products (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                DocumentNo nvarchar(150) NOT NULL,
                SaleCode varchar(20) NOT NULL,
                ProductGroup varchar(10) NOT NULL
            );
            """);
        foreach (var table in (string[])["CartItems", "CheckoutSessionItems", "OrderItems"])
            await IntegrationDb.ExecAsync(c,
                $"""
                 CREATE TABLE shop.{table} (
                     Id uniqueidentifier NOT NULL PRIMARY KEY,
                     ProductId uniqueidentifier NOT NULL
                 );
                 """);
    }

    private static async Task CreateScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        // EXEC(): CREATE DATABASE may not be parameterised and may not share a batch with other statements.
        await IntegrationDb.ExecAsync(master, $"EXEC('CREATE DATABASE [{database}]');");
    }

    private static async Task DropScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    private static Task<SqlConnection> OpenScratchAsync(string database) =>
        IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));

    /// <summary>A model-less context that exists only to hand out EF's SQL Server migration generator — it is
    /// never connected to anything and never queried.</summary>
    private sealed class ScratchContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=(local);Database=unused;Trusted_Connection=True;");
    }
}
