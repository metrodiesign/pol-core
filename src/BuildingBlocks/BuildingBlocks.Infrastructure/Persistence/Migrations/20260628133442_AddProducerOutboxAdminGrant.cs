using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducerOutboxAdminGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // producer-google-sso REQ-20.2 (critique B1): the producer registration writes its outbox event on the
            // keyed pol_admin (RLS-bypass) connection, in the same transaction as the tenant-less Pending row. RLS
            // bypass skips PREDICATES, not table GRANTs — and producer.OutboxMessages only granted INSERT to pol_app
            // (the tenant write path) + SELECT/UPDATE to pol_worker (the dispatcher). So pol_admin must be granted
            // INSERT here, or ProducerOutboxWriter.Enqueue would be denied at the database. The sentinel-tenant row
            // it inserts passes the BLOCK-after-insert predicate because pol_admin bypasses it.
            migrationBuilder.Sql("GRANT INSERT ON producer.OutboxMessages TO pol_admin;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE INSERT ON producer.OutboxMessages FROM pol_admin;");
        }
    }
}
