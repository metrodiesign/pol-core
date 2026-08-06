using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// products-external-source-of-truth REQ-10.1/10.2/10.6: the merchant user's producer code IS the upstream's
    /// sale code, so it takes that name on both the account and the per-submission snapshot, and takes the
    /// upstream column's shape (<c>varchar(20)</c>, non-unicode) in the same step.
    /// <para>HAND-EDITED, deliberately. <c>dotnet ef migrations add</c> scaffolds a rename as DropColumn +
    /// AddColumn, which discards every existing value — the silent-data-loss trap this repo already hit in
    /// products-sp-53-alignment. REQ-10.2 requires the values to survive, so both columns move with
    /// <c>RenameColumn</c> (<c>sp_rename</c>, which keeps the data) and only then change type. Do not
    /// regenerate this file from the scaffolder.</para>
    /// <para>The narrowing <c>AlterColumn</c> does not truncate silently: SQL Server fails the statement if any
    /// row is longer than 20 characters. Values today are at most 10 ASCII characters (seed) and there is no
    /// production data, so no pre-migration cleanup step is needed.</para>
    /// </summary>
    public partial class RenameProducerCodeToSaleCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProducerCode",
                schema: "merch",
                table: "Users",
                newName: "SaleCode");

            migrationBuilder.AlterColumn<string>(
                name: "SaleCode",
                schema: "merch",
                table: "Users",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "ProducerCode",
                schema: "merch",
                table: "RegistrationAttempts",
                newName: "SaleCode");

            migrationBuilder.AlterColumn<string>(
                name: "SaleCode",
                schema: "merch",
                table: "RegistrationAttempts",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rename back FIRST, then widen: the AlterColumn has to name a column that exists at that point.
            migrationBuilder.RenameColumn(
                name: "SaleCode",
                schema: "merch",
                table: "Users",
                newName: "ProducerCode");

            migrationBuilder.AlterColumn<string>(
                name: "ProducerCode",
                schema: "merch",
                table: "Users",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldUnicode: false,
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "SaleCode",
                schema: "merch",
                table: "RegistrationAttempts",
                newName: "ProducerCode");

            migrationBuilder.AlterColumn<string>(
                name: "ProducerCode",
                schema: "merch",
                table: "RegistrationAttempts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldUnicode: false,
                oldMaxLength: 20,
                oldNullable: true);
        }
    }
}
