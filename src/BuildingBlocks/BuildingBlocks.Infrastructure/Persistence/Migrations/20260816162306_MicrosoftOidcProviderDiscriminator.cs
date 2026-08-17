using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MicrosoftOidcProviderDiscriminator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Subject",
                schema: "merch",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Subject",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_RegistrationAudits_TargetSubject",
                schema: "merch",
                table: "RegistrationAudits");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "merch",
                table: "Users",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "google");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "admin",
                table: "Users",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "google");

            migrationBuilder.AddColumn<Guid>(
                name: "ActorAdminId",
                schema: "merch",
                table: "RegistrationAudits",
                type: "uniqueidentifier",
                nullable: true);

            // REQ-4.8 (R3+P1-2): nullable -> backfill by the pre-discriminator unique Subject -> fail on any
            // unmatched row -> NOT NULL. The scaffolded NOT NULL + Guid.Empty default would corrupt existing rows.
            migrationBuilder.AddColumn<Guid>(
                name: "TargetUserId",
                schema: "merch",
                table: "RegistrationAudits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE ra SET ra.TargetUserId = u.Id
                FROM merch.RegistrationAudits ra
                JOIN merch.Users u ON u.Subject = ra.TargetSubject;
                IF EXISTS (SELECT 1 FROM merch.RegistrationAudits WHERE TargetUserId IS NULL)
                    THROW 50001, 'Migration blocked: merch.RegistrationAudits has rows whose TargetSubject matches no merch.Users row - resolve the orphans before upgrading.', 1;

                UPDATE ra SET ra.ActorAdminId = a.Id
                FROM merch.RegistrationAudits ra
                JOIN admin.Users a ON a.Subject = ra.ActorSubject
                WHERE ra.ActorSubject IS NOT NULL;
                IF EXISTS (
                    SELECT 1 FROM merch.RegistrationAudits
                    WHERE Action IN (N'approved', N'rejected', N'revealed', N'suspended')
                      AND ActorAdminId IS NULL)
                    THROW 50001, 'Migration blocked: an admin-performed merch.RegistrationAudits row has no matching admin.Users actor - resolve the actor before upgrading.', 1;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "TargetUserId",
                schema: "merch",
                table: "RegistrationAudits",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Provider_Subject",
                schema: "merch",
                table: "Users",
                columns: new[] { "Provider", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Provider_Subject",
                schema: "admin",
                table: "Users",
                columns: new[] { "Provider", "Subject" },
                unique: true,
                filter: "[Subject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationAudits_TargetUserId",
                schema: "merch",
                table: "RegistrationAudits",
                column: "TargetUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_RegistrationAudits_Users_TargetUserId",
                schema: "merch",
                table: "RegistrationAudits",
                column: "TargetUserId",
                principalSchema: "merch",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Executable rollback guard (R4+P1-3): once two providers share a subject the pre-discriminator
            // unique Subject index cannot be restored — block Down() BEFORE any DDL. Production never runs
            // Down(); restore a backup instead (docs/runbooks/deploy-self-host.md).
            migrationBuilder.Sql("""
                IF EXISTS (SELECT Subject FROM admin.Users WHERE Subject IS NOT NULL
                           GROUP BY Subject HAVING COUNT(*) > 1)
                    THROW 50001, 'Down blocked: duplicate Subject across providers in admin.Users - forward-fix or restore backup.', 1;
                IF EXISTS (SELECT Subject FROM merch.Users GROUP BY Subject HAVING COUNT(*) > 1)
                    THROW 50001, 'Down blocked: duplicate Subject across providers in merch.Users - forward-fix or restore backup.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_RegistrationAudits_Users_TargetUserId",
                schema: "merch",
                table: "RegistrationAudits");

            migrationBuilder.DropIndex(
                name: "IX_Users_Provider_Subject",
                schema: "merch",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Provider_Subject",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_RegistrationAudits_TargetUserId",
                schema: "merch",
                table: "RegistrationAudits");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "merch",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActorAdminId",
                schema: "merch",
                table: "RegistrationAudits");

            migrationBuilder.DropColumn(
                name: "TargetUserId",
                schema: "merch",
                table: "RegistrationAudits");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Subject",
                schema: "merch",
                table: "Users",
                column: "Subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Subject",
                schema: "admin",
                table: "Users",
                column: "Subject",
                unique: true,
                filter: "[Subject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationAudits_TargetSubject",
                schema: "merch",
                table: "RegistrationAudits",
                column: "TargetSubject");
        }
    }
}
