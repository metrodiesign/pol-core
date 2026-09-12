using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropOrgReferenceMasterData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Divisions_DivisionId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Levels_LevelId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Offices_OfficeId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Positions_PositionId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropTable(
                name: "Divisions",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "Levels",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "Offices",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "Positions",
                schema: "cfg");

            migrationBuilder.DropIndex(
                name: "IX_Users_DivisionId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_LevelId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_OfficeId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_PositionId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DivisionId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LevelId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OfficeId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PositionId",
                schema: "admin",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DivisionId",
                schema: "admin",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LevelId",
                schema: "admin",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OfficeId",
                schema: "admin",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PositionId",
                schema: "admin",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Divisions",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LegacyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Divisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Levels",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Levels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Offices",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LegacyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_DivisionId",
                schema: "admin",
                table: "Users",
                column: "DivisionId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_LevelId",
                schema: "admin",
                table: "Users",
                column: "LevelId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_OfficeId",
                schema: "admin",
                table: "Users",
                column: "OfficeId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_PositionId",
                schema: "admin",
                table: "Users",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Divisions_Code",
                schema: "cfg",
                table: "Divisions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Divisions_LegacyKey",
                schema: "cfg",
                table: "Divisions",
                column: "LegacyKey",
                unique: true,
                filter: "[LegacyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Levels_Code",
                schema: "cfg",
                table: "Levels",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Offices_Code",
                schema: "cfg",
                table: "Offices",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Offices_LegacyKey",
                schema: "cfg",
                table: "Offices",
                column: "LegacyKey",
                unique: true,
                filter: "[LegacyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Code",
                schema: "cfg",
                table: "Positions",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Divisions_DivisionId",
                schema: "admin",
                table: "Users",
                column: "DivisionId",
                principalSchema: "cfg",
                principalTable: "Divisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Levels_LevelId",
                schema: "admin",
                table: "Users",
                column: "LevelId",
                principalSchema: "cfg",
                principalTable: "Levels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Offices_OfficeId",
                schema: "admin",
                table: "Users",
                column: "OfficeId",
                principalSchema: "cfg",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Positions_PositionId",
                schema: "admin",
                table: "Users",
                column: "PositionId",
                principalSchema: "cfg",
                principalTable: "Positions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
