using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkforceTenantBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM admin.Users
                    WHERE Provider = N'microsoft'
                      AND Subject IS NOT NULL
                      AND (
                          DATALENGTH(Subject) <> 72
                          OR TRY_CONVERT(uniqueidentifier, Subject) IS NULL
                          OR Subject COLLATE Latin1_General_100_CI_AS
                             <> CONVERT(nvarchar(36), TRY_CONVERT(uniqueidentifier, Subject))
                                COLLATE Latin1_General_100_CI_AS
                      )
                )
                    THROW 50000, 'Microsoft admin subjects must be exact UUID D values before workforce tenant migration.', 1;

                IF EXISTS (
                    SELECT ConvertedSubject
                    FROM (
                        SELECT TRY_CONVERT(uniqueidentifier, Subject) AS ConvertedSubject
                        FROM admin.Users
                        WHERE Provider = N'microsoft' AND Subject IS NOT NULL
                    ) valuesToCheck
                    GROUP BY ConvertedSubject
                    HAVING COUNT(*) > 1
                )
                    THROW 50000, 'Duplicate Microsoft admin identities block workforce tenant migration.', 1;

                UPDATE admin.Users
                SET Subject = LOWER(CONVERT(nvarchar(36), TRY_CONVERT(uniqueidentifier, Subject)))
                WHERE Provider = N'microsoft' AND Subject IS NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "WorkforceTenantBindings",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkforceTenantBindings", x => x.Id);
                    table.CheckConstraint("CK_WorkforceTenantBindings_Singleton", "[Id] = 1");
                });

            migrationBuilder.Sql("GRANT SELECT, INSERT ON admin.WorkforceTenantBindings TO pol_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE SELECT, INSERT ON admin.WorkforceTenantBindings FROM pol_app;");

            migrationBuilder.DropTable(
                name: "WorkforceTenantBindings",
                schema: "admin");
        }
    }
}
