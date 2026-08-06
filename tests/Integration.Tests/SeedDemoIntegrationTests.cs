using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Products.Application.Ports;
using Products.Domain;
using Products.Infrastructure.Sp;

namespace Integration.Tests;

/// <summary>
/// products-external-source-of-truth REQ-6.9/6.10 — the demo seed (<c>docker/bootstrap/seed-demo.sql</c>) run for
/// real against a freshly-migrated VCentralPay, exactly the way <c>scripts/seed-demo.sh</c> and the bootstrap
/// container run it. Two claims no unit test can make:
/// <list type="bullet">
/// <item>REQ-6.9 — the seed no longer touches the dropped <c>shop.Products</c> mirror. A stray reference would
/// fail the run with "Invalid object name 'shop.Products'" (msg 208); the seed's own self-check THROWs if any
/// buy-path row is left without the DocumentNo/SaleCode/ProductGroup identifier that replaced <c>ProductId</c>.</item>
/// <item>REQ-6.10 — every seeded merchant user's <c>SaleCode</c> is one the upstream roster actually issues
/// (77001-77006), so a demo login searches the live catalogue with a code the source knows and sees rows,
/// instead of the silent zero-results failure the old <c>PRD-VP-*</c> placeholders caused.</item>
/// </list>
/// Tagged Integration: the default unit run skips this; CI runs it against a live, migrated SQL service. The seed
/// is idempotent (delete-by-prefix then re-insert in one transaction) and scoped to its own demo Id prefixes, so
/// re-running it here does not disturb other suites' data.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SeedDemoIntegrationTests
{
    private static readonly string[] UpstreamSaleCodes = ["77001", "77002", "77003", "77004", "77005", "77006"];

    [Fact]
    public async Task The_demo_seed_runs_clean_and_binds_real_upstream_sale_codes()
    {
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);

        // REQ-6.9 — run every batch. A reference to the dropped shop.Products would raise msg 208, and the seed's
        // own THROW self-check (blank identifier / any target table at 0 rows) would surface as a SqlException.
        await RunSeedAsync(c);

        // REQ-6.1/6.9 — the mirror table really is gone (OBJECT_ID of a missing object is SQL NULL -> DBNull).
        Assert.IsType<DBNull>(await IntegrationDb.ScalarAsync(c, "SELECT OBJECT_ID(N'shop.Products');"));

        // REQ-6.10 — the seed bound at least one merchant user, and every bound SaleCode is a real upstream code.
        var boundSaleCodes = await ReadColumnAsync(c,
            "SELECT SaleCode FROM merch.Users WHERE Id LIKE 'e5000000-%' AND SaleCode IS NOT NULL;");
        Assert.NotEmpty(boundSaleCodes);
        Assert.All(boundSaleCodes, code => Assert.Contains(code, UpstreamSaleCodes));

        // REQ-6.9 — the identifier that replaced ProductId is populated on every demo buy-path row.
        Assert.Equal(0, (int)(await IntegrationDb.ScalarAsync(c,
            """
            SELECT
                (SELECT COUNT(*) FROM shop.CartItems
                 WHERE Id LIKE 'eb000000-%'
                   AND (DocumentNo IS NULL OR LTRIM(RTRIM(DocumentNo)) = N''
                        OR SaleCode IS NULL OR LTRIM(RTRIM(SaleCode)) = ''
                        OR ProductGroup IS NULL OR LTRIM(RTRIM(ProductGroup)) = ''))
              + (SELECT COUNT(*) FROM shop.OrderItems
                 WHERE Id LIKE 'ef000000-%'
                   AND (DocumentNo IS NULL OR LTRIM(RTRIM(DocumentNo)) = N''
                        OR ProductGroup IS NULL OR LTRIM(RTRIM(ProductGroup)) = ''));
            """))!);
    }

    /// <summary>
    /// The end-to-end claim the SQL-only test above cannot make (products-external-source-of-truth task 5
    /// Verify: line): after a fresh bootstrap, a demo merchant user who logs in and lists products sees a
    /// non-empty catalogue, and a checkout of a seeded cart resolves every line instead of 409-ing. The manual
    /// recipe drives that through real merchant OIDC + HTTP; there is no Google credential in this environment,
    /// so this runs the same two source-facing reads the endpoints run — the REAL <see cref="SpDocumentGateway"/>
    /// against the live hippodb/mammothdb sim instances — fed the values the seed actually wrote:
    /// <list type="bullet">
    /// <item>REQ-6.10 — GET /products (<c>ListProductsHandler</c> -> <c>gateway.SearchAsync</c> with the merchant
    /// user's own <c>@SaleCode</c>) returns rows for every seeded SaleCode, not the silent zero-results the old
    /// <c>PRD-*</c> placeholders produced.</item>
    /// <item>REQ-6.9 — every seeded cart line's DocumentNo is one the upstream still resolves (a live, in-window,
    /// non-PAID document), which is exactly the condition the checkout path (<c>Program.cs</c>: a line whose
    /// lookup is null or PAID -> 409) needs to snapshot the cart instead of rejecting it. The SQL-only test proves
    /// the identifier is non-blank; only this one proves it is a real upstream document.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Seeded_sale_codes_and_cart_documents_resolve_live_against_the_upstream()
    {
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await RunSeedAsync(c);

        var gateway = new SpDocumentGateway(
            Options.Create(new SpDocumentOptions
            {
                BranchCode = "000",
                MotorConnectionString = IntegrationDb.ForCatalog("hippodb"),
                NonMotorConnectionString = IntegrationDb.ForCatalog("mammothdb"),
            }),
            NullLogger<SpDocumentGateway>.Instance);

        // REQ-6.10 — GET /products for every seeded merchant SaleCode returns a non-empty page (Motor catalogue,
        // the side every SaleCode 77001-77006 has UNPAID rows on). A code the source never issued would page empty.
        var saleCodes = await ReadColumnAsync(c,
            "SELECT DISTINCT SaleCode FROM merch.Users WHERE Id LIKE 'e5000000-%' AND SaleCode IS NOT NULL;");
        Assert.NotEmpty(saleCodes);
        foreach (var saleCode in saleCodes)
        {
            var page = await gateway.SearchAsync(MotorSearch(saleCode), CancellationToken.None);
            Assert.NotEmpty(page.Items);
        }

        // REQ-6.9 — every seeded cart line resolves live: a non-null document that is not PAID at source, the
        // exact pair of conditions the checkout path requires to avoid a 409 (Program.cs: doc is null || PAID ->
        // 409). LookupAsync routes to hippodb/mammothdb by ProductGroup and applies the SP's mandatory search
        // window, so an out-of-window number (the F1-class bug) would come back null and fail here.
        var cartLines = await ReadCartLinesAsync(c);
        Assert.NotEmpty(cartLines);
        foreach (var (documentNo, saleCode, group) in cartLines)
        {
            var doc = await gateway.LookupAsync(
                new SpDocumentLookupRequest(documentNo, Enum.Parse<ProductGroup>(group), saleCode),
                CancellationToken.None);
            Assert.NotNull(doc);
            Assert.NotEqual("PAID", doc!.PaymentStatus);
        }
    }

    /// <summary>The catalogue search GET /products issues for a Motor listing: the merchant user's own sale code,
    /// UNPAID (the default), both enum axes ALL, first page, FAST count — the shape of
    /// <c>ListProductsHandler</c>'s <c>SpDocumentSearchRequest</c> for an <c>insuranceType=Motor</c> filter.</summary>
    private static SpDocumentSearchRequest MotorSearch(string saleCode) => new(
        Target: InsuranceType.Motor,
        SaleCode: saleCode,
        SearchText: null,
        InsuredName: null,
        CoverageStartFrom: null,
        CoverageStartTo: null,
        CoverageEndFrom: null,
        CoverageEndTo: null,
        PaymentStatus: "UNPAID",
        DocumentType: "ALL",
        ProductGroup: "ALL",
        PolicyNo: null,
        ApplicationNo: null,
        PaidDateFrom: null,
        PaidDateTo: null,
        PageNo: 1,
        PageSize: 25,
        CountMode: "FAST");

    private static async Task<List<(string DocumentNo, string SaleCode, string ProductGroup)>> ReadCartLinesAsync(
        SqlConnection c)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT DocumentNo, SaleCode, ProductGroup FROM shop.CartItems WHERE Id LIKE 'eb000000-%';";
        var lines = new List<(string, string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return lines;
    }

    /// <summary>Runs <c>docker/bootstrap/seed-demo.sql</c> the way <c>scripts/seed-demo.sh</c> and the bootstrap
    /// container do: substitute <c>$(DbName)</c>, split on <c>GO</c>, execute one batch at a time. Idempotent
    /// (delete-by-prefix then re-insert) and scoped to its own demo Id prefixes, so re-running it per fact does
    /// not disturb other suites' data.</summary>
    private static async Task RunSeedAsync(SqlConnection c)
    {
        var db = Environment.GetEnvironmentVariable("POL_DB") ?? "VCentralPay";
        var script = (await File.ReadAllTextAsync(SeedScriptPath()))
            .Replace("$(DbName)", db, StringComparison.Ordinal);
        foreach (var batch in SplitBatches(script))
            await IntegrationDb.ExecAsync(c, batch);
    }

    /// <summary>Splits a sqlcmd script on its <c>GO</c> batch separators (a line that is only <c>GO</c>), the way
    /// sqlcmd does — ADO.NET executes one batch at a time and does not understand <c>GO</c> itself.</summary>
    private static IEnumerable<string> SplitBatches(string script)
    {
        var current = new List<string>();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = string.Join('\n', current).Trim();
                if (batch.Length > 0) yield return batch;
                current.Clear();
            }
            else
            {
                current.Add(line);
            }
        }
        var tail = string.Join('\n', current).Trim();
        if (tail.Length > 0) yield return tail;
    }

    private static async Task<List<string>> ReadColumnAsync(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private static string SeedScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docker", "bootstrap", "seed-demo.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate docker/bootstrap/seed-demo.sql above the test output directory.");
    }
}
