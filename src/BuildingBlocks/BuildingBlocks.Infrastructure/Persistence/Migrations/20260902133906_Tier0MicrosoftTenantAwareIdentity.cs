using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Tier0MicrosoftTenantAwareIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This is deliberately the first command: no destructive DDL may run against an unverifiable
            // historical WorkforceEmailKey state.
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'admin.WorkforceIdentityMigrations', N'U') IS NULL
                   OR OBJECT_ID(N'admin.WorkforceIdentitySubjectRollback', N'U') IS NULL
                   OR COL_LENGTH(N'admin.Users', N'WorkforceEmailKey') IS NULL
                    THROW 51000, 'Historical workforce identity state is missing.', 1;

                IF (SELECT COUNT(*) FROM admin.WorkforceIdentityMigrations) <> 1
                   OR NOT EXISTS (SELECT 1 FROM admin.WorkforceIdentityMigrations WHERE Id = 1)
                    THROW 51000, 'Historical workforce identity state is invalid.', 1;

                DECLARE @completedAt datetime2(7), @snapshot int, @converted int, @noOp int;
                DECLARE @trimChars nvarchar(32) =
                    NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13) + NCHAR(32) + NCHAR(133)
                    + NCHAR(160) + NCHAR(5760) + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195)
                    + NCHAR(8196) + NCHAR(8197) + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201)
                    + NCHAR(8202) + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288);
                SELECT @completedAt = CompletedAt, @snapshot = SnapshotCount,
                       @converted = ConvertedCount, @noOp = NoOpCount
                FROM admin.WorkforceIdentityMigrations WHERE Id = 1;

                IF @snapshot < 0 OR @converted < 0 OR @noOp < 0
                   OR @snapshot <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback)
                    THROW 51000, 'Historical workforce identity counts are invalid.', 1;

                IF @completedAt IS NULL
                BEGIN
                    IF @converted <> 0 OR @noOp <> 0
                       OR EXISTS (SELECT 1 FROM admin.Users WHERE WorkforceEmailKey IS NOT NULL)
                       OR EXISTS
                       (
                           SELECT 1
                           FROM admin.WorkforceIdentitySubjectRollback
                           WHERE CanonicalSubject IS NOT NULL OR ConversionKind IS NOT NULL
                       )
                        THROW 51000, 'Pending workforce identity state is invalid.', 1;
                END
                ELSE
                BEGIN
                    IF @converted + @noOp <> @snapshot
                       OR @converted <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback
                                         WHERE ConversionKind = N'converted')
                       OR @noOp <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback
                                    WHERE ConversionKind = N'no-op')
                       OR EXISTS
                       (
                           SELECT 1
                           FROM admin.WorkforceIdentitySubjectRollback
                           WHERE LegacySubject IS NULL OR CanonicalSubject IS NULL
                              OR ConversionKind COLLATE Latin1_General_100_BIN2 NOT IN (N'converted', N'no-op')
                       )
                        THROW 51000, 'Completed workforce identity state is invalid.', 1;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM admin.WorkforceIdentitySubjectRollback AS rollbackRows
                        LEFT JOIN admin.Users AS users ON users.Id = rollbackRows.AdminUserId
                        WHERE users.Id IS NULL
                           OR users.Subject IS NULL
                           OR users.WorkforceEmailKey IS NULL
                           OR users.Subject COLLATE Latin1_General_100_BIN2
                              <> rollbackRows.CanonicalSubject COLLATE Latin1_General_100_BIN2
                           OR users.WorkforceEmailKey COLLATE Latin1_General_100_BIN2
                              <> rollbackRows.CanonicalSubject COLLATE Latin1_General_100_BIN2
                    )
                        THROW 51000, 'Completed workforce identity snapshot drifted.', 1;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM admin.Users AS users
                        CROSS APPLY (SELECT TRIM(@trimChars FROM users.Email) AS TrimmedEmail) AS trimmed
                        CROSS APPLY
                        (
                            SELECT CASE
                                WHEN LEN(trimmed.TrimmedEmail) BETWEEN 15 AND 254
                                 AND trimmed.TrimmedEmail COLLATE Latin1_General_100_BIN2
                                     NOT LIKE N'%[^ -~]%' COLLATE Latin1_General_100_BIN2
                                 AND RIGHT(LOWER(trimmed.TrimmedEmail), 14) COLLATE Latin1_General_100_BIN2
                                     = N'@viriyah.co.th'
                                 AND LEN(trimmed.TrimmedEmail)
                                     - LEN(REPLACE(trimmed.TrimmedEmail, N'@', N'')) = 1
                                 AND CHARINDEX(N' ', trimmed.TrimmedEmail) = 0
                                THEN LOWER(trimmed.TrimmedEmail)
                                ELSE NULL
                            END AS ExpectedKey
                        ) AS expected
                        WHERE (users.WorkforceEmailKey IS NULL AND expected.ExpectedKey IS NOT NULL)
                           OR (users.WorkforceEmailKey IS NOT NULL AND expected.ExpectedKey IS NULL)
                           OR users.WorkforceEmailKey COLLATE Latin1_General_100_BIN2
                              <> expected.ExpectedKey COLLATE Latin1_General_100_BIN2
                    )
                        THROW 51000, 'Historical workforce email key drifted.', 1;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM admin.Users
                        WHERE Provider COLLATE Latin1_General_100_BIN2 = N'microsoft'
                          AND Subject IS NOT NULL
                          AND (WorkforceEmailKey IS NULL
                               OR Subject COLLATE Latin1_General_100_BIN2
                                  <> WorkforceEmailKey COLLATE Latin1_General_100_BIN2)
                    )
                        THROW 51000, 'Historical Microsoft identity drifted.', 1;
                END;
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_WorkforceTenantBindings_TenantId",
                schema: "admin",
                table: "WorkforceTenantBindings",
                column: "TenantId");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "admin",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_TenantId_MicrosoftProvider",
                schema: "admin",
                table: "Users",
                sql: "[TenantId] IS NULL OR [Provider] COLLATE Latin1_General_100_BIN2 = N'microsoft'");

            migrationBuilder.DropIndex(
                name: "IX_Users_Provider_Subject",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_WorkforceEmailKey",
                schema: "admin",
                table: "Users");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                schema: "admin",
                table: "Users",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320);

            migrationBuilder.DropColumn(
                name: "WorkforceEmailKey",
                schema: "admin",
                table: "Users");

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

            migrationBuilder.AddForeignKey(
                name: "FK_Users_WorkforceTenantBindings_TenantId",
                schema: "admin",
                table: "Users",
                column: "TenantId",
                principalSchema: "admin",
                principalTable: "WorkforceTenantBindings",
                principalColumn: "TenantId");

            migrationBuilder.Sql(
                """
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

                INSERT admin.WorkforceTenantIdentityMigrations
                    (Id, CompletedAt, SnapshotCount, MappedCount, NoOpCount)
                VALUES (1, NULL, 0, 0, 0);

                GRANT SELECT ON admin.WorkforceTenantIdentityMigrations TO pol_app;
                GRANT SELECT ON admin.WorkforceTenantIdentitySnapshot TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This must remain the first command. Any failure leaves every HEAD object and value untouched.
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations', N'U') IS NULL
                   OR OBJECT_ID(N'admin.WorkforceTenantIdentitySnapshot', N'U') IS NULL
                   OR COL_LENGTH(N'admin.Users', N'TenantId') IS NULL
                    THROW 51000, 'Tenant-aware identity rollback state is missing.', 1;

                IF (SELECT COUNT(*) FROM admin.WorkforceTenantIdentityMigrations) <> 1
                   OR NOT EXISTS (SELECT 1 FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1)
                   OR EXISTS
                   (
                       SELECT 1 FROM admin.WorkforceTenantIdentityMigrations
                       WHERE SnapshotCount < 0 OR MappedCount < 0 OR NoOpCount < 0
                          OR SnapshotCount <> (SELECT COUNT(*) FROM admin.WorkforceTenantIdentitySnapshot)
                          OR MappedCount + NoOpCount <> SnapshotCount
                          OR MappedCount <> 0
                   )
                    THROW 51000, 'Tenant-aware identity rollback state is unsafe.', 1;

                IF EXISTS (SELECT 1 FROM admin.Users WHERE TenantId IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM admin.Users WHERE Email IS NULL)
                   OR EXISTS (SELECT Email FROM admin.Users GROUP BY Email HAVING COUNT_BIG(*) > 1)
                    THROW 51000, 'Tenant-aware identity rollback requires verified mapping or backup restore.', 1;

                IF OBJECT_ID(N'admin.WorkforceIdentityMigrations', N'U') IS NULL
                   OR OBJECT_ID(N'admin.WorkforceIdentitySubjectRollback', N'U') IS NULL
                   OR (SELECT COUNT(*) FROM admin.WorkforceIdentityMigrations) <> 1
                    THROW 51000, 'Historical workforce identity rollback state is invalid.', 1;

                DECLARE @historicalCompletedAt datetime2(7), @historicalSnapshot int,
                        @historicalConverted int, @historicalNoOp int;
                SELECT @historicalCompletedAt = CompletedAt, @historicalSnapshot = SnapshotCount,
                       @historicalConverted = ConvertedCount, @historicalNoOp = NoOpCount
                FROM admin.WorkforceIdentityMigrations WHERE Id = 1;
                IF @historicalSnapshot < 0 OR @historicalConverted < 0 OR @historicalNoOp < 0
                   OR @historicalSnapshot <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback)
                   OR (@historicalCompletedAt IS NULL AND (@historicalConverted <> 0 OR @historicalNoOp <> 0))
                   OR (@historicalCompletedAt IS NOT NULL AND
                       (@historicalConverted + @historicalNoOp <> @historicalSnapshot
                        OR @historicalConverted <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback
                                                    WHERE ConversionKind = N'converted')
                        OR @historicalNoOp <> (SELECT COUNT(*) FROM admin.WorkforceIdentitySubjectRollback
                                               WHERE ConversionKind = N'no-op')
                        OR EXISTS
                           (SELECT 1 FROM admin.WorkforceIdentitySubjectRollback
                            WHERE LegacySubject IS NULL OR CanonicalSubject IS NULL
                               OR ConversionKind COLLATE Latin1_General_100_BIN2 NOT IN (N'converted', N'no-op'))))
                    THROW 51000, 'Historical workforce identity rollback state is invalid.', 1;

                DECLARE @trimChars nvarchar(32) =
                    NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13) + NCHAR(32) + NCHAR(133)
                    + NCHAR(160) + NCHAR(5760) + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195)
                    + NCHAR(8196) + NCHAR(8197) + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201)
                    + NCHAR(8202) + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288);

                IF @historicalCompletedAt IS NOT NULL AND EXISTS
                (
                    SELECT expected.ExpectedKey
                    FROM admin.Users AS users
                    CROSS APPLY (SELECT TRIM(@trimChars FROM users.Email) AS TrimmedEmail) AS trimmed
                    CROSS APPLY
                    (
                        SELECT CASE
                            WHEN LEN(trimmed.TrimmedEmail) BETWEEN 15 AND 254
                             AND trimmed.TrimmedEmail COLLATE Latin1_General_100_BIN2
                                 NOT LIKE N'%[^ -~]%' COLLATE Latin1_General_100_BIN2
                             AND RIGHT(LOWER(trimmed.TrimmedEmail), 14) COLLATE Latin1_General_100_BIN2
                                 = N'@viriyah.co.th'
                             AND LEN(trimmed.TrimmedEmail)
                                 - LEN(REPLACE(trimmed.TrimmedEmail, N'@', N'')) = 1
                             AND CHARINDEX(N' ', trimmed.TrimmedEmail) = 0
                            THEN LOWER(trimmed.TrimmedEmail)
                            ELSE NULL
                        END AS ExpectedKey
                    ) AS expected
                    WHERE expected.ExpectedKey IS NOT NULL
                    GROUP BY expected.ExpectedKey
                    HAVING COUNT_BIG(*) > 1
                )
                    THROW 51000, 'Legacy workforce email uniqueness cannot be restored safely.', 1;
                """);

            migrationBuilder.Sql(
                """
                REVOKE SELECT ON admin.WorkforceTenantIdentitySnapshot FROM pol_app;
                REVOKE SELECT ON admin.WorkforceTenantIdentityMigrations FROM pol_app;
                DROP TABLE admin.WorkforceTenantIdentitySnapshot;
                DROP TABLE admin.WorkforceTenantIdentityMigrations;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Users_WorkforceTenantBindings_TenantId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Provider_TenantId_Subject",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_TenantId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_TenantId_MicrosoftProvider",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_WorkforceTenantBindings_TenantId",
                schema: "admin",
                table: "WorkforceTenantBindings");

            migrationBuilder.AddColumn<string>(
                name: "WorkforceEmailKey",
                schema: "admin",
                table: "Users",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            // Dynamic SQL prevents idempotent-script batch compilation from binding the just-added column early.
            migrationBuilder.Sql(
                """
                EXEC(N'
                    DECLARE @trimChars nvarchar(32) =
                        NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13) + NCHAR(32) + NCHAR(133)
                        + NCHAR(160) + NCHAR(5760) + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195)
                        + NCHAR(8196) + NCHAR(8197) + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201)
                        + NCHAR(8202) + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288);
                    UPDATE users
                    SET WorkforceEmailKey = CASE WHEN state.CompletedAt IS NULL THEN NULL ELSE expected.ExpectedKey END
                    FROM admin.Users AS users
                    CROSS JOIN admin.WorkforceIdentityMigrations AS state
                    CROSS APPLY (SELECT TRIM(@trimChars FROM users.Email) AS TrimmedEmail) AS trimmed
                    CROSS APPLY
                    (
                        SELECT CASE
                            WHEN LEN(trimmed.TrimmedEmail) BETWEEN 15 AND 254
                             AND trimmed.TrimmedEmail COLLATE Latin1_General_100_BIN2
                                 NOT LIKE N''%[^ -~]%'' COLLATE Latin1_General_100_BIN2
                             AND RIGHT(LOWER(trimmed.TrimmedEmail), 14) COLLATE Latin1_General_100_BIN2
                                 = N''@viriyah.co.th''
                             AND LEN(trimmed.TrimmedEmail)
                                 - LEN(REPLACE(trimmed.TrimmedEmail, N''@'', N'''')) = 1
                             AND CHARINDEX(N'' '', trimmed.TrimmedEmail) = 0
                            THEN LOWER(trimmed.TrimmedEmail)
                            ELSE NULL
                        END AS ExpectedKey
                    ) AS expected
                    WHERE state.Id = 1;');
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                schema: "admin",
                table: "Users",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "admin",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Provider_Subject",
                schema: "admin",
                table: "Users",
                columns: new[] { "Provider", "Subject" },
                unique: true,
                filter: "[Subject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_WorkforceEmailKey",
                schema: "admin",
                table: "Users",
                column: "WorkforceEmailKey",
                unique: true,
                filter: "[WorkforceEmailKey] IS NOT NULL");
        }
    }
}
