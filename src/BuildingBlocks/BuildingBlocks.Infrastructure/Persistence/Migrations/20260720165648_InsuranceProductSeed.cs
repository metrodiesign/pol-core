using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// insurance-pivot task 2 (REQ-4): dev/demo sample insurance plans, empty EF model so it never diffs
    /// (same hand-written style as <see cref="SeedData"/>). Fixed, well-formed RFC-4122 GUIDs — 'e5000000-…'
    /// namespaces this table (continuing the SeedData 'a1000000'/'b2000000'/'c3000000'/'d4000000' per-table
    /// letter convention), and one fixed seed merchant id so the rows are queryable together locally
    /// (<c>Products.MerchantId</c> has no DB-level FK — see <c>Products.Infrastructure/ProductConfiguration.cs</c>
    /// — so this is just a shared scoping value, not a seeded <c>merch.Merchants</c> row). One row per generic
    /// insurance line (PA/Travel/Motor/Property, REQ-1.1's "no per-line schema").
    /// </summary>
    public partial class InsuranceProductSeed : Migration
    {
        private const string SeedMerchantId = "e5000000-0000-4000-8000-000000000000";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                INSERT INTO shop.Products
                  (Id, MerchantId, Name, PriceAmount, PriceCurrency, SumInsuredAmount, SumInsuredCurrency,
                   CoverageDurationDays, InsurerName, IsActive, CreatedAt)
                VALUES
                  ('e5000000-0000-4000-8000-000000000001', '{SeedMerchantId}', N'ประกันอุบัติเหตุส่วนบุคคล (PA)',
                    599.0000, 'THB', 500000.0000, 'THB', 365, N'Muang Thai Insurance', 1, '2026-07-20T00:00:00'),
                  ('e5000000-0000-4000-8000-000000000002', '{SeedMerchantId}', N'ประกันเดินทางต่างประเทศ',
                    899.0000, 'THB', 2000000.0000, 'THB', 14, N'Viriyah Insurance', 1, '2026-07-20T00:00:00'),
                  ('e5000000-0000-4000-8000-000000000003', '{SeedMerchantId}', N'ประกันภัยรถยนต์ชั้น 1',
                    15900.0000, 'THB', 800000.0000, 'THB', 365, N'Thai Insurance', 1, '2026-07-20T00:00:00'),
                  ('e5000000-0000-4000-8000-000000000004', '{SeedMerchantId}', N'ประกันอัคคีภัยบ้านอยู่อาศัย',
                    1200.0000, 'THB', 1500000.0000, 'THB', 365, N'Dhipaya Insurance', 1, '2026-07-20T00:00:00');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM shop.Products WHERE Id LIKE 'e5000000-%';");
        }
    }
}
