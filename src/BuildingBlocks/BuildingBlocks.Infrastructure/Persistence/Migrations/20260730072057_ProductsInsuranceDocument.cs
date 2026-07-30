using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Products pivot: a Product is now a sellable insurance document (VCentralPay SP guide §2/§5.2 —
    /// APPLICATION/POLICY/RENEWAL/ENDORSEMENT awaiting payment), no longer an insurance-plan template.
    /// Pre-prod big-bang: the old shape's rows (plan seed 'e5000000-%' + demo rows) cannot be mapped onto
    /// the document shape, so the table is dropped and recreated (approved decision), grants re-applied
    /// (DROP TABLE loses them — the GrantInsuranceLineTables lesson), and fresh document samples seeded
    /// from real usp_Motor_SearchDocument output ('e6000000-%' continues the per-table GUID convention).
    /// </summary>
    public partial class ProductsInsuranceDocument : Migration
    {
        private const string SeedMerchantId = "e5000000-0000-4000-8000-000000000000";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Products", schema: "shop");

            migrationBuilder.CreateTable(
                name: "Products",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductGroup = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    DocumentType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    DocumentNo = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    PolicyYear = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: true),
                    ReferenceBranch = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: true),
                    ReferencePre = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    PolicySequenceNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    ReferenceYear = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: true),
                    ReferenceNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    BranchCode = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    SaleCode = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    SaleFullName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BrokerCode = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    BrokerName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PolicyBranch = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    PolicyType = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    PolicyNumber = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: true),
                    ApplicationNumber = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: true),
                    PreviousPolicyNumber = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: true),
                    EndorsementNumber = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: true),
                    StartDate = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    EndDate = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    ShowName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LicensePlateNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TotalPremiumAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    TotalPremiumCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    NetPremiumAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    NetPremiumCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    StampAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    StampCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    TaxVatAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    TaxVatCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    CommissionAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    CommissionCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    CommissionPercent = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    PaymentStatus = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    PaidDate = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_Products", x => x.Id));

            migrationBuilder.CreateIndex(
                name: "IX_Products_MerchantId_IsActive",
                schema: "shop",
                table: "Products",
                columns: new[] { "MerchantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_MerchantId_PaymentStatus",
                schema: "shop",
                table: "Products",
                columns: new[] { "MerchantId", "PaymentStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_MerchantId_DocumentNo",
                schema: "shop",
                table: "Products",
                columns: new[] { "MerchantId", "DocumentNo" },
                unique: true);

            // DROP TABLE dropped the grants with the table — re-apply what RlsTeardownAndOnePrincipal
            // established for shop.Products (pol_app is the sole runtime principal since PR #112).
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Products TO pol_app;");

            // Dev/demo document samples shaped after real usp_Motor_SearchDocument output (screenshot-verified):
            // PolicyYear/ReferenceYear are 2-digit Buddhist-era strings, PolicyType is a code string,
            // ReferencePre only appears on ENDORSEMENT, LicensePlateNumber is Motor-only.
            migrationBuilder.Sql($"""
                INSERT INTO shop.Products
                  (Id, MerchantId, ProductGroup, DocumentType, DocumentNo, PolicyYear,
                   ReferenceBranch, ReferencePre, PolicySequenceNo, ReferenceYear, ReferenceNo,
                   BranchCode, SaleCode, SaleFullName, BrokerCode, BrokerName, PolicyBranch, PolicyType,
                   PolicyNumber, ApplicationNumber, PreviousPolicyNumber, EndorsementNumber,
                   StartDate, EndDate, ShowName, LicensePlateNumber,
                   TotalPremiumAmount, TotalPremiumCurrency, NetPremiumAmount, NetPremiumCurrency,
                   StampAmount, StampCurrency, TaxVatAmount, TaxVatCurrency,
                   CommissionAmount, CommissionCurrency, CommissionPercent,
                   PaymentStatus, PaidDate, IsActive, CreatedAt)
                VALUES
                  ('e6000000-0000-4000-8000-000000000001', '{SeedMerchantId}', 'VMI', 'POLICY',
                    N'00098-69100/กธ/037674-10', '69', '100', NULL, '037674', '69', '037674',
                    '100', '00098', N'บริษัท มาร์ช พีบี จำกัด (ประเทศไทย)', '013', N'บริษัท มาร์ช พีบี จำกัด',
                    N'สำนักงานใหญ่', '10', '00098-68100/037674', NULL, NULL, NULL,
                    '2026-07-01T00:00:00', '2027-07-01T00:00:00', N'สมชาย ใจดี', N'1กก 1234',
                    15900.0000, 'THB', 14800.0000, 'THB', 59.0000, 'THB', 1041.0000, 'THB',
                    1776.0000, 'THB', 12.000000, 'UNPAID', NULL, 1, '2026-07-30T00:00:00'),
                  ('e6000000-0000-4000-8000-000000000002', '{SeedMerchantId}', 'CMI', 'POLICY',
                    N'00098-69100/กธ/E013697', '69', '100', NULL, 'E013697', '69', 'E013697',
                    '100', '00098', N'บริษัท มาร์ช พีบี จำกัด (ประเทศไทย)', '013', N'บริษัท มาร์ช พีบี จำกัด',
                    N'สำนักงานใหญ่', NULL, '00098-69100/E013697', NULL, NULL, NULL,
                    '2026-07-15T00:00:00', '2027-07-15T00:00:00', N'สมหญิง รักดี', N'2ขข 5678',
                    645.2100, 'THB', 600.0000, 'THB', 3.0000, 'THB', 42.2100, 'THB',
                    72.0000, 'THB', 12.000000, 'UNPAID', NULL, 1, '2026-07-30T00:00:00'),
                  ('e6000000-0000-4000-8000-000000000003', '{SeedMerchantId}', 'CMI', 'ENDORSEMENT',
                    N'69100/สล/0001514', '69', '100', '100', 'E008520', '69', '0001574',
                    '100', '00098', N'บริษัท มาร์ช พีบี จำกัด (ประเทศไทย)', '013', N'บริษัท มาร์ช พีบี จำกัด',
                    N'สำนักงานใหญ่', NULL, '00098-69100/E008520', NULL, '00098-68100/E007001', 'E008520',
                    '2026-07-20T00:00:00', '2027-07-20T00:00:00', N'วิชัย มั่นคง', N'3คค 9012',
                    120.0000, 'THB', 110.0000, 'THB', 1.0000, 'THB', 9.0000, 'THB',
                    13.2000, 'THB', 12.000000, 'UNPAID', NULL, 1, '2026-07-30T00:00:00'),
                  ('e6000000-0000-4000-8000-000000000004', '{SeedMerchantId}', 'VMI', 'RENEWAL',
                    N'00098-68100/ตอ/068575-10', '68', '100', NULL, '068575', '68', '068575',
                    '100', '00098', N'บริษัท มาร์ช พีบี จำกัด (ประเทศไทย)', '013', N'บริษัท มาร์ช พีบี จำกัด',
                    N'สำนักงานใหญ่', '10', '00098-68100/068575', NULL, '00098-67100/052001', NULL,
                    '2026-08-01T00:00:00', '2027-08-01T00:00:00', N'ประยุทธ สายทอง', N'4งง 3456',
                    17250.5000, 'THB', 16000.0000, 'THB', 65.0000, 'THB', 1185.5000, 'THB',
                    1920.0000, 'THB', 12.000000, 'UNPAID', NULL, 1, '2026-07-30T00:00:00'),
                  ('e6000000-0000-4000-8000-000000000005', '{SeedMerchantId}', 'FIRE', 'POLICY',
                    N'S001-69100/อค/012345', '69', '100', NULL, '012345', '69', '012345',
                    '100', 'S001', N'บริษัท มาร์ช พีบี จำกัด (ประเทศไทย)', '013', N'บริษัท มาร์ช พีบี จำกัด',
                    N'สำนักงานใหญ่', NULL, 'S001-69100/012345', NULL, NULL, NULL,
                    '2026-07-10T00:00:00', '2027-07-10T00:00:00', N'อรทัย แสงจันทร์', NULL,
                    1284.0000, 'THB', 1200.0000, 'THB', 5.0000, 'THB', 79.0000, 'THB',
                    144.0000, 'THB', 12.000000, 'UNPAID', NULL, 1, '2026-07-30T00:00:00'),
                  ('e6000000-0000-4000-8000-000000000006', '{SeedMerchantId}', 'MISC', 'APPLICATION',
                    N'S001-69100/บต/000777', '69', '100', NULL, '000777', '69', '000777',
                    '100', 'S001', N'บริษัท มาร์ช พีบี จำกัด (ประเทศไทย)', '013', N'บริษัท มาร์ช พีบี จำกัด',
                    N'สำนักงานใหญ่', NULL, NULL, 'S001-69100/000777', NULL, NULL,
                    NULL, NULL, N'กมล พูนสุข', NULL,
                    2140.0000, 'THB', 2000.0000, 'THB', 8.0000, 'THB', 132.0000, 'THB',
                    240.0000, 'THB', 12.000000, 'UNPAID', NULL, 1, '2026-07-30T00:00:00');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Products", schema: "shop");

            // Restore the pre-pivot insurance-plan shape (without its seed rows).
            migrationBuilder.CreateTable(
                name: "Products",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PriceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PriceCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    SumInsuredAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    SumInsuredCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    CoverageDurationDays = table.Column<int>(type: "int", nullable: false),
                    InsurerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_Products", x => x.Id));

            migrationBuilder.CreateIndex(
                name: "IX_Products_MerchantId_IsActive",
                schema: "shop",
                table: "Products",
                columns: new[] { "MerchantId", "IsActive" });

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Products TO pol_app;");
        }
    }
}
