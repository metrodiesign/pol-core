using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducerIdentityTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalLogins",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalLogins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProducerPermissionGroups",
                schema: "producer",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LabelTh = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProducerPermissionGroups", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ProducerRoles",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Color = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProducerRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationAudits",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TargetSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationTickets",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    HostedDomain = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationTickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantUserProfiles",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PersonType = table.Column<int>(type: "int", nullable: true),
                    IdNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProducerCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LicenseNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PhotoObjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PhotoContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantUsers",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProducerPermissions",
                schema: "producer",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LabelTh = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProducerPermissions", x => x.Key);
                    table.ForeignKey(
                        name: "FK_ProducerPermissions_ProducerPermissionGroups_GroupKey",
                        column: x => x.GroupKey,
                        principalSchema: "producer",
                        principalTable: "ProducerPermissionGroups",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProducerRoleAssignments",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProducerRoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProducerRoleAssignments_ProducerRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "producer",
                        principalTable: "ProducerRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProducerRolePermissions",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissionKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProducerRolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProducerRolePermissions_ProducerPermissions_PermissionKey",
                        column: x => x.PermissionKey,
                        principalSchema: "producer",
                        principalTable: "ProducerPermissions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProducerRolePermissions_ProducerRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "producer",
                        principalTable: "ProducerRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLogins_Provider_Subject",
                schema: "producer",
                table: "ExternalLogins",
                columns: new[] { "Provider", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProducerPermissions_GroupKey",
                schema: "producer",
                table: "ProducerPermissions",
                column: "GroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_ProducerRoleAssignments_RoleId",
                schema: "producer",
                table: "ProducerRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ProducerRoleAssignments_TenantUserId_RoleId",
                schema: "producer",
                table: "ProducerRoleAssignments",
                columns: new[] { "TenantUserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProducerRoleAssignments_TenantUserId_TenantId",
                schema: "producer",
                table: "ProducerRoleAssignments",
                columns: new[] { "TenantUserId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProducerRolePermissions_PermissionKey",
                schema: "producer",
                table: "ProducerRolePermissions",
                column: "PermissionKey");

            migrationBuilder.CreateIndex(
                name: "IX_ProducerRolePermissions_RoleId_PermissionKey",
                schema: "producer",
                table: "ProducerRolePermissions",
                columns: new[] { "RoleId", "PermissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProducerRoles_Code",
                schema: "producer",
                table: "ProducerRoles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserProfiles_TenantUserId",
                schema: "producer",
                table: "TenantUserProfiles",
                column: "TenantUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_Subject",
                schema: "producer",
                table: "TenantUsers",
                column: "Subject",
                unique: true);

            // --- RLS floor + control-plane (raw SQL; not part of the EF model diff) ---
            // TenantUsers joins the existing RLS floor additively, scoped on its (nullable) TenantId. A
            // PendingApproval row has TenantId NULL -> NULL = SESSION_CONTEXT is UNKNOWN -> hidden from
            // pol_app (a pending user is correctly invisible to tenants; only pol_admin bypass sees it for
            // approval). FILTER = own-tenant read once approved; BLOCK = a tenant cannot forge a foreign id.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY producer.TenantIsolationPolicy\n" +
                "    ADD FILTER PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers,\n" +
                "    ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers AFTER INSERT,\n" +
                "    ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers AFTER UPDATE;");

            // Control-plane notice table for the Admin-side registration consumer (Task 4). Raw SQL with NO
            // EF entity this slice (its consumer lands in Task 4); pol_admin + pol_worker only, NEVER pol_app
            // (S5). Unique TenantUserId = idempotent one-notice-per-registration.
            migrationBuilder.Sql("""
                CREATE TABLE producer.ProducerRegistrationNotices (
                    Id            uniqueidentifier NOT NULL CONSTRAINT PK_ProducerRegistrationNotices PRIMARY KEY,
                    TenantUserId  uniqueidentifier NOT NULL,
                    Subject       nvarchar(256)    NOT NULL,
                    Email         nvarchar(320)    NOT NULL,
                    DisplayName   nvarchar(200)    NOT NULL,
                    HostedDomain  nvarchar(256)    NULL,
                    OccurredAt    datetime2        NOT NULL,
                    CreatedAt     datetime2        NOT NULL
                );
                CREATE UNIQUE INDEX IX_ProducerRegistrationNotices_TenantUserId
                    ON producer.ProducerRegistrationNotices (TenantUserId);
                """);

            // Least-privilege grants for the identity realm. The child identity tables are admin-only
            // control-plane (pol_app gets NO grant -> no per-tenant predicate needed; a tenant principal
            // cannot touch them at all). RBAC tables are granted separately in AddProducerRoleRbacTables.
            migrationBuilder.Sql("""
                -- pol_app (TenantConsole): read its OWN tenant's users (RLS-filtered). No write; no child tables.
                GRANT SELECT ON producer.TenantUsers TO pol_app;

                -- pol_admin (registration/approval/resolve, bypass role): cross-tenant on the identity tables.
                GRANT SELECT, INSERT, UPDATE ON producer.TenantUsers         TO pol_admin;
                GRANT SELECT, INSERT         ON producer.ExternalLogins       TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON producer.TenantUserProfiles   TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON producer.RegistrationTickets  TO pol_admin;
                GRANT SELECT, INSERT         ON producer.RegistrationAudits    TO pol_admin;

                -- ProducerRegistrationNotices (control-plane, S5): pol_admin + pol_worker (outbox dispatcher)
                -- write/read; pol_app NEVER granted.
                GRANT SELECT, INSERT ON producer.ProducerRegistrationNotices TO pol_admin;
                GRANT SELECT, INSERT ON producer.ProducerRegistrationNotices TO pol_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Detach the TenantUsers predicates BEFORE dropping the table (a table under a security policy
            // cannot be dropped); never drop the shared TenantIsolationPolicy itself.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY producer.TenantIsolationPolicy\n" +
                "    DROP FILTER PREDICATE ON producer.TenantUsers,\n" +
                "    DROP BLOCK PREDICATE ON producer.TenantUsers AFTER INSERT,\n" +
                "    DROP BLOCK PREDICATE ON producer.TenantUsers AFTER UPDATE;");

            migrationBuilder.Sql("""
                REVOKE SELECT ON producer.TenantUsers FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE ON producer.TenantUsers         FROM pol_admin;
                REVOKE SELECT, INSERT         ON producer.ExternalLogins       FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE ON producer.TenantUserProfiles   FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE ON producer.RegistrationTickets  FROM pol_admin;
                REVOKE SELECT, INSERT         ON producer.RegistrationAudits    FROM pol_admin;
                REVOKE SELECT, INSERT ON producer.ProducerRegistrationNotices FROM pol_admin;
                REVOKE SELECT, INSERT ON producer.ProducerRegistrationNotices FROM pol_worker;
                """);

            migrationBuilder.Sql("DROP TABLE producer.ProducerRegistrationNotices;");

            migrationBuilder.DropTable(
                name: "ExternalLogins",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "ProducerRoleAssignments",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "ProducerRolePermissions",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "RegistrationAudits",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "RegistrationTickets",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "TenantUserProfiles",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "TenantUsers",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "ProducerPermissions",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "ProducerRoles",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "ProducerPermissionGroups",
                schema: "producer");
        }
    }
}
