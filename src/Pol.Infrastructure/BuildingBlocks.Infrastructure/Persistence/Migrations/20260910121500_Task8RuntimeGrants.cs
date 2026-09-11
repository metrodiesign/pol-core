using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol.Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations;

public partial class Task8RuntimeGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Runtime principal floor for Task2–Task7 tables that were added after the original security grant.
        // Keep each table grant at the operation level; do not grant schema ownership or db_owner.
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON acct.AgentRegistrations TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON acct.AgentRegistrationAttempts TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON merch.Branches TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON merch.Sales TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON checkout.PaymentLinks TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT ON checkout.PaymentLinkReplays TO pol_app;");

        // Task7 materializer, dispatcher and operator operations.
        migrationBuilder.Sql("GRANT SELECT, INSERT ON txn.Notifications TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT ON txn.NotificationInboxMessages TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT ON txn.TemplateVersions TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON txn.Deliveries TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT ON txn.DeliveryAttempts TO pol_app;");
        migrationBuilder.Sql("GRANT SELECT, INSERT ON txn.NotificationReviewNotes TO pol_app;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON acct.AgentRegistrations FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON acct.AgentRegistrationAttempts FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON merch.Branches FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON merch.Sales FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON checkout.PaymentLinks FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT ON checkout.PaymentLinkReplays FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT ON txn.Notifications FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT ON txn.NotificationInboxMessages FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT ON txn.TemplateVersions FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT, UPDATE ON txn.Deliveries FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT ON txn.DeliveryAttempts FROM pol_app;");
        migrationBuilder.Sql("REVOKE SELECT, INSERT ON txn.NotificationReviewNotes FROM pol_app;");
    }
}
