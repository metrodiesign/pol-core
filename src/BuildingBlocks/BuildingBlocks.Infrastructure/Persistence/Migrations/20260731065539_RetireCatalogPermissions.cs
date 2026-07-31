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
            // Copy every grant out before deleting it. The role API let a merchant create a CUSTOM role holding
            // either key, so the rows deleted here are not just the four seed grants — hard-coding the seed four
            // into Down() would silently drop a customer's role configuration on rollback. iam.RetiredCatalogGrants
            // is the rollback backup: Down() replays it verbatim and drops it. It survives on purpose after Up so
            // the rollback stays possible; delete it manually once the release is past its rollback window.
            // Not an EF entity and no GRANT to pol_app — migrations own it, runtime never reads it.
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'iam.RetiredCatalogGrants', N'U') IS NOT NULL
                    DROP TABLE iam.RetiredCatalogGrants;

                SELECT Id, RoleId, PermissionKey
                INTO iam.RetiredCatalogGrants
                FROM iam.RolePermissions
                WHERE PermissionKey IN ('product.create','product.update');
                """);

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
            // Parents before children: group -> permissions -> grants (reverse of Up). The group/key rows are
            // fixed reference data, so they come from the same literals SeedData.cs used (SortOrder 6/14/15).
            migrationBuilder.Sql("""
                INSERT INTO iam.PermissionGroups ([Key], Scope, LabelTh, SortOrder) VALUES
                  ('catalog', 1, N'สินค้า', 6);

                INSERT INTO iam.Permissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('product.create', 'catalog', N'สร้างสินค้า', 14),
                  ('product.update', 'catalog', N'แก้ไขสินค้า', 15);
                """);

            // Grants come from the Up() backup, not from literals: it holds the seed grants AND any custom-role
            // grant that existed at Up time, with the original Ids. Restoring from it is what makes the rollback
            // lossless. If a hand-run recovery already dropped the table, fall back to the two seed roles so a
            // rollback still leaves the anchor roles usable.
            migrationBuilder.Sql($"""
                IF OBJECT_ID(N'iam.RetiredCatalogGrants', N'U') IS NOT NULL
                BEGIN
                    INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey)
                    SELECT Id, RoleId, PermissionKey FROM iam.RetiredCatalogGrants;

                    DROP TABLE iam.RetiredCatalogGrants;
                END
                ELSE
                BEGIN
                    INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                      (NEWID(), '{MerchantManagerRoleId}', 'product.create'),
                      (NEWID(), '{MerchantManagerRoleId}', 'product.update'),
                      (NEWID(), '{MerchantStaffRoleId}', 'product.create'),
                      (NEWID(), '{MerchantStaffRoleId}', 'product.update');
                END
                """);
        }
    }
}
