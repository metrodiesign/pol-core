using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task3MerchantMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Branches",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.Id);
                    table.UniqueConstraint("AK_Branches_MerchantId_Id", x => new { x.MerchantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Branches_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merch",
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sales",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sales", x => x.Id);
                    table.UniqueConstraint("AK_Sales_MerchantId_Id", x => new { x.MerchantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Sales_Branches_MerchantId_BranchId",
                        columns: x => new { x.MerchantId, x.BranchId },
                        principalSchema: "merch",
                        principalTable: "Branches",
                        principalColumns: new[] { "MerchantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sales_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merch",
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Branches_MerchantId_Code",
                schema: "merch",
                table: "Branches",
                columns: new[] { "MerchantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_MerchantId_BranchId",
                schema: "merch",
                table: "Sales",
                columns: new[] { "MerchantId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_MerchantId_Code",
                schema: "merch",
                table: "Sales",
                columns: new[] { "MerchantId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sales",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "Branches",
                schema: "merch");
        }
    }
}
