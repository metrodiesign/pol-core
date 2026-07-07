using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDataProtectionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            // Control-plane key ring: pol_admin only, NO tenant RLS predicate, pol_app gets NO grant
            // (mirrors the admin identity tables). The Data Protection framework only ever appends keys and
            // reads them back — never UPDATE/DELETE — so SELECT, INSERT suffices.
            migrationBuilder.Sql("GRANT SELECT, INSERT ON VCentralPay.DataProtectionKeys TO pol_admin;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE SELECT, INSERT ON VCentralPay.DataProtectionKeys FROM pol_admin;");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys",
                schema: "VCentralPay");
        }
    }
}
