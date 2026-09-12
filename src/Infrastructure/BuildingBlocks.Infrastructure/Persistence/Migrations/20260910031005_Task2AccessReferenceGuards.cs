using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task2AccessReferenceGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                schema: "access",
                table: "BranchAccess",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                schema: "acct",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                schema: "access",
                table: "AccessRoles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddUniqueConstraint(
                name: "AK_MerchantAccess_Id_MerchantId",
                schema: "access",
                table: "MerchantAccess",
                columns: new[] { "Id", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchAccess_MerchantAccessId_MerchantId",
                schema: "access",
                table: "BranchAccess",
                columns: new[] { "MerchantAccessId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_MerchantId_SaleId",
                schema: "acct",
                table: "Agents",
                columns: new[] { "MerchantId", "SaleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_MerchantAccessId_MerchantId",
                schema: "access",
                table: "AccessRoles",
                columns: new[] { "MerchantAccessId", "MerchantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AccessRoles_MerchantAccess_MerchantAccessId_MerchantId",
                schema: "access",
                table: "AccessRoles",
                columns: new[] { "MerchantAccessId", "MerchantId" },
                principalSchema: "access",
                principalTable: "MerchantAccess",
                principalColumns: new[] { "Id", "MerchantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BranchAccess_MerchantAccess_MerchantAccessId_MerchantId",
                schema: "access",
                table: "BranchAccess",
                columns: new[] { "MerchantAccessId", "MerchantId" },
                principalSchema: "access",
                principalTable: "MerchantAccess",
                principalColumns: new[] { "Id", "MerchantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PlatformAccess_Employees_EmployeeAccountId",
                schema: "access",
                table: "PlatformAccess",
                column: "EmployeeAccountId",
                principalSchema: "acct",
                principalTable: "Employees",
                principalColumn: "AccountId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccessRoles_MerchantAccess_MerchantAccessId_MerchantId",
                schema: "access",
                table: "AccessRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_BranchAccess_MerchantAccess_MerchantAccessId_MerchantId",
                schema: "access",
                table: "BranchAccess");

            migrationBuilder.DropForeignKey(
                name: "FK_PlatformAccess_Employees_EmployeeAccountId",
                schema: "access",
                table: "PlatformAccess");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MerchantAccess_Id_MerchantId",
                schema: "access",
                table: "MerchantAccess");

            migrationBuilder.DropIndex(
                name: "IX_BranchAccess_MerchantAccessId_MerchantId",
                schema: "access",
                table: "BranchAccess");

            migrationBuilder.DropIndex(
                name: "IX_Agents_MerchantId_SaleId",
                schema: "acct",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_AccessRoles_MerchantAccessId_MerchantId",
                schema: "access",
                table: "AccessRoles");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                schema: "access",
                table: "BranchAccess");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                schema: "acct",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                schema: "access",
                table: "AccessRoles");
        }
    }
}
