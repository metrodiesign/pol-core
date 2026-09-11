using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol.Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations;

public partial class Task8CommerceRuntimeGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("GRANT SELECT, UPDATE ON OBJECT::[shop].[Orders] TO [pol_app];");
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::[shop].[OrderItems] TO [pol_app];");
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON OBJECT::[txn].[Transactions] TO [pol_app];");
        migrationBuilder.Sql("GRANT SELECT, INSERT ON OBJECT::[txn].[TransactionEvents] TO [pol_app];");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("REVOKE SELECT, UPDATE ON OBJECT::[shop].[Orders] FROM [pol_app];");
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE, DELETE ON OBJECT::[shop].[OrderItems] FROM [pol_app];");
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON OBJECT::[txn].[Transactions] FROM [pol_app];");
        migrationBuilder.Sql("REVOKE SELECT, INSERT ON OBJECT::[txn].[TransactionEvents] FROM [pol_app];");
    }
}
