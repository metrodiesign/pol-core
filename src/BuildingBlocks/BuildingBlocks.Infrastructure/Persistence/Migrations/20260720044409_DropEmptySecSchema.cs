using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropEmptySecSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RlsTeardownAndOnePrincipal dropped every object inside `sec` (predicate function,
            // security policy, the 3 EXECUTE-AS procs) but left the schema container itself —
            // it has held zero objects since. Drop it here so a fresh reset (SecurityObjects
            // still runs `CREATE SCHEMA sec` before this migration removes it again) doesn't
            // leave an empty, unused schema in the DB tree.
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS sec;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF SCHEMA_ID(N'sec') IS NULL EXEC(N'CREATE SCHEMA sec AUTHORIZATION dbo;');");
        }
    }
}
