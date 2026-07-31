using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Retires the orphan catalog permissions: product.create/product.update (group catalog) no longer gate any
    // endpoint since POST /api/v1/products was removed for good (commit 152b692 — the catalogue is read-only
    // over HTTP, documents come from the source SP, not a form). Scaffolded empty (no EF model diff — Keys.cs is
    // a domain constant list, not mapped) then hand-edited, same pattern as SeedPolicyPermissions.cs. Deletes
    // grants on EVERY role holding the key (not just the two seed roles) so a custom merchant role that granted
    // it is cleaned up too.
    public partial class RetireCatalogPermissions : Migration
    {
        private const string MerchantManagerRoleId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        private const string MerchantStaffRoleId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Children before parents (FK-safe): grants -> permissions -> group.
            migrationBuilder.Sql("""
                DELETE FROM iam.RolePermissions WHERE PermissionKey IN ('product.create','product.update');
                DELETE FROM iam.Permissions WHERE [Key] IN ('product.create','product.update');
                DELETE FROM iam.PermissionGroups WHERE [Key] = 'catalog';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Parents before children: group -> permissions -> grants (reverse of Up). Restores the exact seed
            // group/keys/grants SeedData.cs originally gave merchant_manager/merchant_staff (SortOrder 6/14/15
            // preserved).
            migrationBuilder.Sql("""
                INSERT INTO iam.PermissionGroups ([Key], Scope, LabelTh, SortOrder) VALUES
                  ('catalog', 1, N'สินค้า', 6);

                INSERT INTO iam.Permissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('product.create', 'catalog', N'สร้างสินค้า', 14),
                  ('product.update', 'catalog', N'แก้ไขสินค้า', 15);
                """);
            migrationBuilder.Sql($"""
                INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  (NEWID(), '{MerchantManagerRoleId}', 'product.create'),
                  (NEWID(), '{MerchantManagerRoleId}', 'product.update'),
                  (NEWID(), '{MerchantStaffRoleId}', 'product.create'),
                  (NEWID(), '{MerchantStaffRoleId}', 'product.update');
                """);
        }
    }
}
