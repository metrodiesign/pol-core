using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantPspSecurityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalExecutionRecords",
                schema: "txn",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalExecutionRecords", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalExecutionRecords_ApprovalId",
                schema: "txn",
                table: "ApprovalExecutionRecords",
                column: "ApprovalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalExecutionRecords_MerchantId_CreatedAt",
                schema: "txn",
                table: "ApprovalExecutionRecords",
                columns: new[] { "MerchantId", "CreatedAt" });

            migrationBuilder.Sql("GRANT SELECT, INSERT ON txn.ApprovalExecutionRecords TO pol_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE SELECT, INSERT ON txn.ApprovalExecutionRecords FROM pol_app;");

            migrationBuilder.DropTable(
                name: "ApprovalExecutionRecords",
                schema: "txn");
        }
    }
}
