using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminMasterDataAndProfileFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DivisionId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LevelId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OfficeId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PositionId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Divisions",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Divisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Levels",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Levels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Offices",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_DivisionId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                column: "DivisionId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_LevelId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                column: "LevelId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_OfficeId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                column: "OfficeId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_PositionId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Divisions_Code",
                schema: "VCentralPay",
                table: "Divisions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Levels_Code",
                schema: "VCentralPay",
                table: "Levels",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Offices_Code",
                schema: "VCentralPay",
                table: "Offices",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Code",
                schema: "VCentralPay",
                table: "Positions",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAccounts_Divisions_DivisionId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                column: "DivisionId",
                principalSchema: "VCentralPay",
                principalTable: "Divisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAccounts_Levels_LevelId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                column: "LevelId",
                principalSchema: "VCentralPay",
                principalTable: "Levels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAccounts_Offices_OfficeId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                column: "OfficeId",
                principalSchema: "VCentralPay",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAccounts_Positions_PositionId",
                schema: "VCentralPay",
                table: "AdminAccounts",
                column: "PositionId",
                principalSchema: "VCentralPay",
                principalTable: "Positions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Control-plane: pol_admin only, NO tenant RLS predicate. Runtime CRUD manages these lists (no hard
            // delete — masters are soft-deactivated via IsActive, and the AdminAccount FK is Restrict).
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON VCentralPay.Positions TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON VCentralPay.Offices   TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON VCentralPay.Levels    TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON VCentralPay.Divisions TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdminAccounts_Divisions_DivisionId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_AdminAccounts_Levels_LevelId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_AdminAccounts_Offices_OfficeId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_AdminAccounts_Positions_PositionId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropTable(
                name: "Divisions",
                schema: "VCentralPay");

            migrationBuilder.DropTable(
                name: "Levels",
                schema: "VCentralPay");

            migrationBuilder.DropTable(
                name: "Offices",
                schema: "VCentralPay");

            migrationBuilder.DropTable(
                name: "Positions",
                schema: "VCentralPay");

            migrationBuilder.DropIndex(
                name: "IX_AdminAccounts_DivisionId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropIndex(
                name: "IX_AdminAccounts_LevelId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropIndex(
                name: "IX_AdminAccounts_OfficeId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropIndex(
                name: "IX_AdminAccounts_PositionId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropColumn(
                name: "DivisionId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropColumn(
                name: "LevelId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropColumn(
                name: "OfficeId",
                schema: "VCentralPay",
                table: "AdminAccounts");

            migrationBuilder.DropColumn(
                name: "PositionId",
                schema: "VCentralPay",
                table: "AdminAccounts");
        }
    }
}
