using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireLegacyAdminIdentityPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The raw workforce-identity migration bookkeeping tables (created by Tier0WorkforceEmailIdentity /
            // Tier0MicrosoftTenantAwareIdentity outside the model) FK into admin.Users, so they must be dropped
            // before the EF DropTable calls below can remove admin.Users.
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS admin.WorkforceIdentitySubjectRollback;
                DROP TABLE IF EXISTS admin.WorkforceTenantIdentitySnapshot;
                DROP TABLE IF EXISTS admin.WorkforceIdentityMigrations;
                DROP TABLE IF EXISTS admin.WorkforceTenantIdentityMigrations;
                """);

            migrationBuilder.DropTable(
                name: "AuthAudits",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "MerchantAccess",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "RoleAssignments",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "WorkforceTenantBindings",
                schema: "admin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthAudits",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MerchantAccess",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantAccess", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "iam",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                    table.UniqueConstraint("AK_WorkforceTenantBindings_TenantId", x => x.TenantId);
                    table.CheckConstraint("CK_WorkforceTenantBindings_Singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    EmployeeId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "microsoft"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Tier = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_Users_TenantId_MicrosoftProvider", "[TenantId] IS NULL OR [Provider] COLLATE Latin1_General_100_BIN2 = N'microsoft'");
                    table.ForeignKey(
                        name: "FK_Users_WorkforceTenantBindings_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "admin",
                        principalTable: "WorkforceTenantBindings",
                        principalColumn: "TenantId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthAudits_AdminUserId",
                schema: "admin",
                table: "AuthAudits",
                column: "AdminUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantAccess_AdminUserId_MerchantId",
                schema: "admin",
                table: "MerchantAccess",
                columns: new[] { "AdminUserId", "MerchantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_AdminUserId_RoleId",
                schema: "admin",
                table: "RoleAssignments",
                columns: new[] { "AdminUserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_RoleId",
                schema: "admin",
                table: "RoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_EmployeeId",
                schema: "admin",
                table: "Users",
                column: "EmployeeId",
                unique: true,
                filter: "[EmployeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Provider_TenantId_Subject",
                schema: "admin",
                table: "Users",
                columns: new[] { "Provider", "TenantId", "Subject" },
                unique: true,
                filter: "[Subject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId",
                schema: "admin",
                table: "Users",
                column: "TenantId");

            // Recreate the raw workforce-identity bookkeeping tables (Up dropped them). Table shapes copied
            // verbatim from Tier0WorkforceEmailIdentity / Tier0MicrosoftTenantAwareIdentity; both FK admin.Users,
            // which is recreated above. The two *Migrations singleton rows (Id = 1, all counts 0) are restored
            // too: their older Down guards THROW on COUNT(*) <> 1, so a rollback that continues past this
            // migration needs the bookkeeping present to evaluate an empty admin plane as safe. The child
            // (SubjectRollback / Snapshot) tables stay empty, matching a fresh/empty admin.Users.
            migrationBuilder.Sql("""
                CREATE TABLE admin.WorkforceIdentityMigrations
                (
                    Id int NOT NULL,
                    CompletedAt datetime2(7) NULL,
                    SnapshotCount int NOT NULL CONSTRAINT DF_WorkforceIdentityMigrations_SnapshotCount DEFAULT 0,
                    ConvertedCount int NOT NULL CONSTRAINT DF_WorkforceIdentityMigrations_ConvertedCount DEFAULT 0,
                    NoOpCount int NOT NULL CONSTRAINT DF_WorkforceIdentityMigrations_NoOpCount DEFAULT 0,
                    CONSTRAINT PK_WorkforceIdentityMigrations PRIMARY KEY (Id),
                    CONSTRAINT CK_WorkforceIdentityMigrations_Singleton
                        CHECK (Id = 1 AND SnapshotCount >= 0 AND ConvertedCount >= 0 AND NoOpCount >= 0)
                );

                CREATE TABLE admin.WorkforceIdentitySubjectRollback
                (
                    AdminUserId uniqueidentifier NOT NULL,
                    LegacySubject nvarchar(256) NULL,
                    CanonicalSubject nvarchar(254) NULL,
                    ConversionKind nvarchar(16) NULL,
                    CONSTRAINT PK_WorkforceIdentitySubjectRollback PRIMARY KEY (AdminUserId),
                    CONSTRAINT FK_WorkforceIdentitySubjectRollback_Users_AdminUserId
                        FOREIGN KEY (AdminUserId) REFERENCES admin.Users (Id)
                );

                CREATE TABLE admin.WorkforceTenantIdentityMigrations
                (
                    Id int NOT NULL,
                    CompletedAt datetime2(7) NULL,
                    SnapshotCount int NOT NULL CONSTRAINT DF_WorkforceTenantIdentityMigrations_SnapshotCount DEFAULT 0,
                    MappedCount int NOT NULL CONSTRAINT DF_WorkforceTenantIdentityMigrations_MappedCount DEFAULT 0,
                    NoOpCount int NOT NULL CONSTRAINT DF_WorkforceTenantIdentityMigrations_NoOpCount DEFAULT 0,
                    CONSTRAINT PK_WorkforceTenantIdentityMigrations PRIMARY KEY (Id),
                    CONSTRAINT CK_WorkforceTenantIdentityMigrations_Singleton
                        CHECK (Id = 1 AND SnapshotCount >= 0 AND MappedCount >= 0 AND NoOpCount >= 0)
                );

                CREATE TABLE admin.WorkforceTenantIdentitySnapshot
                (
                    AdminUserId uniqueidentifier NOT NULL,
                    CONSTRAINT PK_WorkforceTenantIdentitySnapshot PRIMARY KEY (AdminUserId),
                    CONSTRAINT FK_WorkforceTenantIdentitySnapshot_Users_AdminUserId
                        FOREIGN KEY (AdminUserId) REFERENCES admin.Users (Id) ON DELETE NO ACTION
                );

                INSERT admin.WorkforceIdentityMigrations (Id, CompletedAt, SnapshotCount, ConvertedCount, NoOpCount)
                VALUES (1, NULL, 0, 0, 0);
                INSERT admin.WorkforceTenantIdentityMigrations (Id, CompletedAt, SnapshotCount, MappedCount, NoOpCount)
                VALUES (1, NULL, 0, 0, 0);
                """);
        }
    }
}
