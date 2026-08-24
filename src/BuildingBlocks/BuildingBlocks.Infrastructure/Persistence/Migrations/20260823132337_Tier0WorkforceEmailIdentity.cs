using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Tier0WorkforceEmailIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkforceEmailKey",
                schema: "admin",
                table: "Users",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_WorkforceEmailKey",
                schema: "admin",
                table: "Users",
                column: "WorkforceEmailKey",
                unique: true,
                filter: "[WorkforceEmailKey] IS NOT NULL");

            migrationBuilder.Sql(
                """
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

                INSERT admin.WorkforceIdentitySubjectRollback (AdminUserId, LegacySubject)
                SELECT Id, Subject
                FROM admin.Users
                WHERE Provider COLLATE Latin1_General_100_BIN2 = N'microsoft'
                  AND Subject IS NOT NULL;

                INSERT admin.WorkforceIdentityMigrations
                    (Id, CompletedAt, SnapshotCount, ConvertedCount, NoOpCount)
                SELECT 1, NULL, COUNT(*), 0, 0
                FROM admin.WorkforceIdentitySubjectRollback;

                GRANT SELECT ON admin.WorkforceIdentityMigrations TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'admin.WorkforceIdentityMigrations', N'U') IS NULL
                   OR OBJECT_ID(N'admin.WorkforceIdentitySubjectRollback', N'U') IS NULL
                    THROW 51000, 'Workforce identity rollback state is missing.', 1;

                IF (SELECT COUNT(*) FROM admin.WorkforceIdentityMigrations) <> 1
                    THROW 51000, 'Workforce identity rollback state is invalid.', 1;

                DECLARE @completedAt datetime2(7) =
                    (SELECT CompletedAt FROM admin.WorkforceIdentityMigrations WHERE Id = 1);

                IF @completedAt IS NULL
                BEGIN
                    IF EXISTS
                    (
                        SELECT 1
                        FROM
                        (
                            SELECT Id, Subject
                            FROM admin.Users
                            WHERE Provider COLLATE Latin1_General_100_BIN2 = N'microsoft'
                              AND Subject IS NOT NULL
                        ) AS currentRows
                        FULL OUTER JOIN admin.WorkforceIdentitySubjectRollback AS rollbackRows
                            ON rollbackRows.AdminUserId = currentRows.Id
                        WHERE currentRows.Id IS NULL
                           OR rollbackRows.AdminUserId IS NULL
                           OR currentRows.Subject COLLATE Latin1_General_100_BIN2
                              <> rollbackRows.LegacySubject COLLATE Latin1_General_100_BIN2
                    )
                        THROW 51000, 'Workforce identity rollback guard detected pending-state drift.', 1;
                END
                ELSE
                BEGIN
                    IF EXISTS
                    (
                        SELECT 1
                        FROM admin.WorkforceIdentitySubjectRollback
                        WHERE LegacySubject IS NULL
                           OR CanonicalSubject IS NULL
                           OR ConversionKind COLLATE Latin1_General_100_BIN2 NOT IN (N'converted', N'no-op')
                    )
                        THROW 51000, 'Workforce identity rollback manifest is incomplete.', 1;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM admin.WorkforceIdentityMigrations AS state
                        WHERE state.Id <> 1
                           OR state.SnapshotCount <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback)
                           OR state.ConvertedCount <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback
                                                       WHERE ConversionKind = N'converted')
                           OR state.NoOpCount <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback
                                                  WHERE ConversionKind = N'no-op')
                           OR state.ConvertedCount + state.NoOpCount <> state.SnapshotCount
                    )
                        THROW 51000, 'Workforce identity rollback counts are invalid.', 1;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM
                        (
                            SELECT Id, Subject
                            FROM admin.Users
                            WHERE Provider COLLATE Latin1_General_100_BIN2 = N'microsoft'
                              AND Subject IS NOT NULL
                        ) AS currentRows
                        FULL OUTER JOIN admin.WorkforceIdentitySubjectRollback AS rollbackRows
                            ON rollbackRows.AdminUserId = currentRows.Id
                        WHERE currentRows.Id IS NULL
                           OR rollbackRows.AdminUserId IS NULL
                           OR currentRows.Subject COLLATE Latin1_General_100_BIN2
                              <> rollbackRows.CanonicalSubject COLLATE Latin1_General_100_BIN2
                    )
                        THROW 51000, 'Workforce identity rollback guard detected completed-state drift.', 1;

                    UPDATE users
                    SET Subject = rollbackRows.LegacySubject
                    FROM admin.Users AS users
                    INNER JOIN admin.WorkforceIdentitySubjectRollback AS rollbackRows
                        ON rollbackRows.AdminUserId = users.Id;

                    IF @@ROWCOUNT <> (SELECT SnapshotCount FROM admin.WorkforceIdentityMigrations WHERE Id = 1)
                        THROW 51000, 'Workforce identity rollback restore count is invalid.', 1;
                END;

                REVOKE SELECT ON admin.WorkforceIdentityMigrations FROM pol_app;
                DROP TABLE admin.WorkforceIdentitySubjectRollback;
                DROP TABLE admin.WorkforceIdentityMigrations;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Users_WorkforceEmailKey",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WorkforceEmailKey",
                schema: "admin",
                table: "Users");
        }
    }
}
