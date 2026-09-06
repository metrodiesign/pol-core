using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentSessionRoutingSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PspConnectionId",
                schema: "txn",
                table: "PaymentSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PspEnvironment",
                schema: "txn",
                table: "PaymentSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "RoutingSnapshotVersion",
                schema: "txn",
                table: "PaymentSessions",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<Guid>(
                name: "SecretVersionId",
                schema: "txn",
                table: "PaymentSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentSessions_RoutingSnapshotV1",
                schema: "txn",
                table: "PaymentSessions",
                sql: "[RoutingSnapshotVersion] <> 1 OR ([PspConnectionId] IS NOT NULL AND [SecretVersionId] IS NOT NULL AND [PspEnvironment] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentSessions_RoutingSnapshotV1",
                schema: "txn",
                table: "PaymentSessions");

            migrationBuilder.DropColumn(
                name: "PspConnectionId",
                schema: "txn",
                table: "PaymentSessions");

            migrationBuilder.DropColumn(
                name: "PspEnvironment",
                schema: "txn",
                table: "PaymentSessions");

            migrationBuilder.DropColumn(
                name: "RoutingSnapshotVersion",
                schema: "txn",
                table: "PaymentSessions");

            migrationBuilder.DropColumn(
                name: "SecretVersionId",
                schema: "txn",
                table: "PaymentSessions");
        }
    }
}
