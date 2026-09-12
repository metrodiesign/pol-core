using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PolDbContext))]
[Migration("20260811024015_AdminDeliveryRuntimeGrants")]
public sealed class AdminDeliveryRuntimeGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        GRANT SELECT, INSERT, UPDATE         ON iam.ApiClients              TO pol_app;
        GRANT SELECT, INSERT, UPDATE         ON iam.OneTimeSecretTickets    TO pol_app;
        GRANT SELECT, INSERT, UPDATE         ON admin.DeliverySecretVersions TO pol_app;
        GRANT SELECT, INSERT, UPDATE         ON txn.InboundWebhookEvents    TO pol_app;
        GRANT SELECT, INSERT                 ON admin.NotificationDeliveries TO pol_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON admin.NotificationRules     TO pol_app;
        GRANT SELECT, INSERT, UPDATE         ON admin.WebhookDeliveries     TO pol_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON admin.WebhookEndpoints      TO pol_app;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.WebhookEndpoints       FROM pol_app;
        REVOKE SELECT, INSERT, UPDATE         ON admin.WebhookDeliveries      FROM pol_app;
        REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.NotificationRules      FROM pol_app;
        REVOKE SELECT, INSERT                 ON admin.NotificationDeliveries FROM pol_app;
        REVOKE SELECT, INSERT, UPDATE         ON txn.InboundWebhookEvents     FROM pol_app;
        REVOKE SELECT, INSERT, UPDATE         ON admin.DeliverySecretVersions FROM pol_app;
        REVOKE SELECT, INSERT, UPDATE         ON iam.OneTimeSecretTickets     FROM pol_app;
        REVOKE SELECT, INSERT, UPDATE         ON iam.ApiClients               FROM pol_app;
        """);
}
