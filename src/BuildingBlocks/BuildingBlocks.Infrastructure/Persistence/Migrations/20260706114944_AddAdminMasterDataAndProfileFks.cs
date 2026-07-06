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
                schema: "producer",
                table: "AdminAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LevelId",
                schema: "producer",
                table: "AdminAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OfficeId",
                schema: "producer",
                table: "AdminAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PositionId",
                schema: "producer",
                table: "AdminAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Divisions",
                schema: "producer",
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
                schema: "producer",
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
                schema: "producer",
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
                schema: "producer",
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
                schema: "producer",
                table: "AdminAccounts",
                column: "DivisionId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_LevelId",
                schema: "producer",
                table: "AdminAccounts",
                column: "LevelId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_OfficeId",
                schema: "producer",
                table: "AdminAccounts",
                column: "OfficeId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_PositionId",
                schema: "producer",
                table: "AdminAccounts",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Divisions_Code",
                schema: "producer",
                table: "Divisions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Levels_Code",
                schema: "producer",
                table: "Levels",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Offices_Code",
                schema: "producer",
                table: "Offices",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Code",
                schema: "producer",
                table: "Positions",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAccounts_Divisions_DivisionId",
                schema: "producer",
                table: "AdminAccounts",
                column: "DivisionId",
                principalSchema: "producer",
                principalTable: "Divisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAccounts_Levels_LevelId",
                schema: "producer",
                table: "AdminAccounts",
                column: "LevelId",
                principalSchema: "producer",
                principalTable: "Levels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAccounts_Offices_OfficeId",
                schema: "producer",
                table: "AdminAccounts",
                column: "OfficeId",
                principalSchema: "producer",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAccounts_Positions_PositionId",
                schema: "producer",
                table: "AdminAccounts",
                column: "PositionId",
                principalSchema: "producer",
                principalTable: "Positions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Control-plane: pol_admin only, NO tenant RLS predicate. Runtime CRUD manages these lists (no hard
            // delete — masters are soft-deactivated via IsActive, and the AdminAccount FK is Restrict).
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON producer.Positions TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON producer.Offices   TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON producer.Levels    TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON producer.Divisions TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdminAccounts_Divisions_DivisionId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_AdminAccounts_Levels_LevelId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_AdminAccounts_Offices_OfficeId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_AdminAccounts_Positions_PositionId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropTable(
                name: "Divisions",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "Levels",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "Offices",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "Positions",
                schema: "producer");

            migrationBuilder.DropIndex(
                name: "IX_AdminAccounts_DivisionId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropIndex(
                name: "IX_AdminAccounts_LevelId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropIndex(
                name: "IX_AdminAccounts_OfficeId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropIndex(
                name: "IX_AdminAccounts_PositionId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropColumn(
                name: "DivisionId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropColumn(
                name: "LevelId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropColumn(
                name: "OfficeId",
                schema: "producer",
                table: "AdminAccounts");

            migrationBuilder.DropColumn(
                name: "PositionId",
                schema: "producer",
                table: "AdminAccounts");
        }
    }
}
