using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE SEQUENCE shop.OrderNoSeq AS bigint
                    START WITH 1 INCREMENT BY 1 NO CYCLE;

                CREATE TABLE merch.RegistrationNotices (
                    Id           uniqueidentifier NOT NULL CONSTRAINT PK_RegistrationNotices PRIMARY KEY,
                    UserId       uniqueidentifier NOT NULL,
                    Subject      nvarchar(256) NOT NULL,
                    Email        nvarchar(320) NOT NULL,
                    DisplayName  nvarchar(200) NOT NULL,
                    HostedDomain nvarchar(256) NULL,
                    OccurredAt   datetime2 NOT NULL,
                    CreatedAt    datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX IX_RegistrationNotices_UserId
                    ON merch.RegistrationNotices (UserId);
                """);

            // One runtime principal. Permissions mirror concrete application readers/writers;
            // append-only audit/outbox tables intentionally receive no UPDATE/DELETE grant.
            migrationBuilder.Sql("""
                IF USER_ID(N'pol_app') IS NULL
                    THROW 51003, N'SecurityObjects requires database user [pol_app].', 1;

                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Carts                  TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.CartItems              TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Orders                 TO pol_app;
                GRANT SELECT, INSERT                 ON shop.OrderItems             TO pol_app;
                GRANT SELECT, INSERT                 ON shop.OrderItemRevealAudits TO pol_app;
                GRANT UPDATE ON OBJECT::shop.OrderNoSeq TO pol_app;

                GRANT SELECT, INSERT, UPDATE ON txn.PaymentSessions    TO pol_app;
                GRANT SELECT, INSERT         ON txn.PspConnections    TO pol_app;
                GRANT SELECT, INSERT         ON txn.IdempotencyRecords TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON txn.OutboxMessages      TO pol_app;

                GRANT SELECT, INSERT, UPDATE         ON merch.Merchants           TO pol_app;
                GRANT SELECT, INSERT, UPDATE         ON merch.VaultSecrets        TO pol_app;
                GRANT SELECT, INSERT                 ON merch.VaultRevealAudits   TO pol_app;
                GRANT SELECT, INSERT                 ON merch.RegistrationNotices TO pol_app;
                GRANT SELECT, INSERT, UPDATE         ON merch.UserOutbox          TO pol_app;
                GRANT SELECT, INSERT, UPDATE         ON merch.Users               TO pol_app;
                GRANT SELECT, INSERT                 ON merch.ExternalLogins      TO pol_app;
                GRANT SELECT, INSERT                 ON merch.RegistrationAudits  TO pol_app;
                GRANT SELECT, INSERT                 ON merch.RegistrationAttempts TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON merch.Sessions            TO pol_app;
                GRANT SELECT, INSERT                 ON merch.AuthAudits          TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON merch.RoleAssignments     TO pol_app;
                GRANT SELECT, INSERT                 ON merch.ProvisioningAudits  TO pol_app;

                GRANT SELECT, INSERT, UPDATE         ON admin.Users                  TO pol_app;
                GRANT SELECT, INSERT                 ON admin.UserAudits             TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON admin.MerchantAccess         TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON admin.Sessions               TO pol_app;
                GRANT SELECT, INSERT                 ON admin.AuthAudits             TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON admin.RoleAssignments        TO pol_app;
                GRANT SELECT, INSERT, UPDATE         ON admin.ProvisioningOperations TO pol_app;

                GRANT SELECT, INSERT, UPDATE ON cfg.Positions TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.Offices   TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.Levels    TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.Divisions TO pol_app;

                GRANT SELECT                         ON iam.PermissionGroups TO pol_app;
                GRANT SELECT                         ON iam.Permissions      TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iam.Roles           TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iam.RolePermissions TO pol_app;

                GRANT SELECT, INSERT ON dbo.DataProtectionKeys TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF USER_ID(N'pol_app') IS NOT NULL
                BEGIN
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.Carts                  FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.CartItems              FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.Orders                 FROM pol_app;
                    REVOKE SELECT, INSERT                 ON shop.OrderItems             FROM pol_app;
                    REVOKE SELECT, INSERT                 ON shop.OrderItemRevealAudits FROM pol_app;
                    REVOKE UPDATE ON OBJECT::shop.OrderNoSeq FROM pol_app;

                    REVOKE SELECT, INSERT, UPDATE ON txn.PaymentSessions    FROM pol_app;
                    REVOKE SELECT, INSERT         ON txn.PspConnections    FROM pol_app;
                    REVOKE SELECT, INSERT         ON txn.IdempotencyRecords FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON txn.OutboxMessages      FROM pol_app;

                    REVOKE SELECT, INSERT, UPDATE         ON merch.Merchants            FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE         ON merch.VaultSecrets         FROM pol_app;
                    REVOKE SELECT, INSERT                 ON merch.VaultRevealAudits    FROM pol_app;
                    REVOKE SELECT, INSERT                 ON merch.RegistrationNotices  FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE         ON merch.UserOutbox           FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE         ON merch.Users                FROM pol_app;
                    REVOKE SELECT, INSERT                 ON merch.ExternalLogins       FROM pol_app;
                    REVOKE SELECT, INSERT                 ON merch.RegistrationAudits   FROM pol_app;
                    REVOKE SELECT, INSERT                 ON merch.RegistrationAttempts FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON merch.Sessions             FROM pol_app;
                    REVOKE SELECT, INSERT                 ON merch.AuthAudits           FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON merch.RoleAssignments      FROM pol_app;
                    REVOKE SELECT, INSERT                 ON merch.ProvisioningAudits   FROM pol_app;

                    REVOKE SELECT, INSERT, UPDATE         ON admin.Users                  FROM pol_app;
                    REVOKE SELECT, INSERT                 ON admin.UserAudits             FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.MerchantAccess         FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.Sessions               FROM pol_app;
                    REVOKE SELECT, INSERT                 ON admin.AuthAudits             FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.RoleAssignments        FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE         ON admin.ProvisioningOperations FROM pol_app;

                    REVOKE SELECT, INSERT, UPDATE ON cfg.Positions FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.Offices   FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.Levels    FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.Divisions FROM pol_app;

                    REVOKE SELECT                         ON iam.PermissionGroups FROM pol_app;
                    REVOKE SELECT                         ON iam.Permissions      FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON iam.Roles           FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON iam.RolePermissions FROM pol_app;
                    REVOKE SELECT, INSERT ON dbo.DataProtectionKeys FROM pol_app;
                END

                IF OBJECT_ID(N'merch.RegistrationNotices', N'U') IS NOT NULL
                    DROP TABLE merch.RegistrationNotices;
                IF OBJECT_ID(N'shop.OrderNoSeq', N'SO') IS NOT NULL
                    DROP SEQUENCE shop.OrderNoSeq;
                """);
        }
    }
}
