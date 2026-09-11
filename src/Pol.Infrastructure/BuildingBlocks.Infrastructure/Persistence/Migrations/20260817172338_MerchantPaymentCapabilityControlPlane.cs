using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantPaymentCapabilityControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "cfg",
                table: "PaymentProviders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                schema: "cfg",
                table: "PaymentProviders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "cfg",
                table: "PaymentMethods",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                schema: "cfg",
                table: "PaymentMethods",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.UpdateData(
                schema: "cfg",
                table: "PaymentMethods",
                keyColumn: "Id",
                keyValue: new Guid("f1000000-0000-4000-8000-000000000001"),
                columns: new[] { "UpdatedAt", "UpdatedBy" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                schema: "cfg",
                table: "PaymentMethods",
                keyColumn: "Id",
                keyValue: new Guid("f1000000-0000-4000-8000-000000000002"),
                columns: new[] { "UpdatedAt", "UpdatedBy" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                schema: "cfg",
                table: "PaymentMethods",
                keyColumn: "Id",
                keyValue: new Guid("f1000000-0000-4000-8000-000000000003"),
                columns: new[] { "UpdatedAt", "UpdatedBy" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                schema: "cfg",
                table: "PaymentProviders",
                keyColumn: "Id",
                keyValue: new Guid("f4000000-0000-4000-8000-000000000001"),
                columns: new[] { "UpdatedAt", "UpdatedBy" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                schema: "cfg",
                table: "PaymentProviders",
                keyColumn: "Id",
                keyValue: new Guid("f4000000-0000-4000-8000-000000000002"),
                columns: new[] { "UpdatedAt", "UpdatedBy" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "cfg",
                table: "PaymentProviders");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                schema: "cfg",
                table: "PaymentProviders");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "cfg",
                table: "PaymentMethods");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                schema: "cfg",
                table: "PaymentMethods");
        }
    }
}
