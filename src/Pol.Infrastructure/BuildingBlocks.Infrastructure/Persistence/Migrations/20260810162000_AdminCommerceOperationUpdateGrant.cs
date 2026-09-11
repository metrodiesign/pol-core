using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PolDbContext))]
    [Migration("20260810162000_AdminCommerceOperationUpdateGrant")]
    public partial class AdminCommerceOperationUpdateGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT UPDATE ON txn.AdminOperationRecords TO pol_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE UPDATE ON txn.AdminOperationRecords FROM pol_app;");
        }
    }
}
