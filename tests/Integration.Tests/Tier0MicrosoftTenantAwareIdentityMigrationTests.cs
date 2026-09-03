using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class Tier0MicrosoftTenantAwareIdentityMigrationTests
{
    private const string BeforeHistoricalMigration = "20260819145219_WorkforceTenantBinding";
    private const string PreviousMigration = "20260830172117_Tier0EmployeeProfile";
    private const string CurrentMigration = "20260902133906_Tier0MicrosoftTenantAwareIdentity";
    private static readonly Guid PositionId = Guid.Parse("a1000000-0000-4000-8000-000000000001");
    private static readonly Guid OfficeId = Guid.Parse("b2000000-0000-4000-8000-000000000001");
    private static readonly Guid LevelId = Guid.Parse("c3000000-0000-4000-8000-000000000001");
    private static readonly Guid DivisionId = Guid.Parse("d4000000-0000-4000-8000-000000000002");

    [Fact]
    public async Task Committed_idempotent_schema_script_applies_twice_from_an_empty_database()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var script = await File.ReadAllTextAsync(SqlScripts.RepoPath("docker", "migrations", "schema.sql"));

        await database.ExecuteBatchesAsync(script);
        await database.ExecuteBatchesAsync(script);

        await using var verify = await database.OpenAsync();
        Assert.Equal(23, Convert.ToInt32(await ScalarAsync(
            verify, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory;")));
        Assert.NotEqual(DBNull.Value, await ScalarAsync(
            verify, "SELECT OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations', N'U');"));
    }

    [Fact]
    public async Task Up_preserves_rows_and_profile_shape_while_replacing_email_ownership_with_tenant_tuple_constraints()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeHistoricalMigration);
        var legacyId = Guid.NewGuid();
        var googleId = Guid.NewGuid();
        var legacySubject = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid();
        var userAuditId = Guid.NewGuid();
        var authAuditId = Guid.NewGuid();
        await database.InsertUserAsync(legacyId, "microsoft", legacySubject, " Legacy@VIRIYAH.CO.TH ");
        await database.InsertUserAsync(googleId, "google", "google-subject", "same@example.com");
        await database.MigrateAsync(PreviousMigration);
        await database.ExecuteAsync(
            """
            UPDATE admin.Users
            SET Tier = 2, Status = 2, EmployeeId = N'SYNTH001', FirstName = N'Alpha', LastName = N'Fixture',
                PositionId = @positionId, OfficeId = @officeId, LevelId = @levelId, DivisionId = @divisionId,
                Version = 9, AuthorizationVersion = 7
            WHERE Id = @legacyId;
            DECLARE @roleId uniqueidentifier = (SELECT TOP (1) Id FROM iam.Roles ORDER BY Id);
            INSERT admin.RoleAssignments (Id, AdminUserId, RoleId, AssignedById, AssignedAt)
            VALUES (NEWID(), @legacyId, @roleId, @legacyId, SYSUTCDATETIME());
            INSERT admin.MerchantAccess (Id, AdminUserId, MerchantId, AssignedByAdminId, AssignedAt)
            VALUES (NEWID(), @legacyId, @merchantId, @legacyId, SYSUTCDATETIME());
            INSERT admin.Sessions
                (Id, FamilyId, TokenHash, AdminUserId, Status, IssuedAt, IdleExpiresAt, AbsoluteExpiresAt)
            VALUES
                (@sessionId, NEWID(), HASHBYTES('SHA2_256', N'synthetic-session'), @legacyId, 1,
                 SYSUTCDATETIME(), DATEADD(MINUTE, 30, SYSUTCDATETIME()), DATEADD(HOUR, 8, SYSUTCDATETIME()));
            INSERT admin.UserAudits
                (Id, Action, ActorType, ActorId, TargetAdminId, MerchantId, TargetRoleId, CorrelationId, OccurredAt)
            VALUES
                (@userAuditId, N'synthetic-before-up', N'system', @legacyId, @legacyId, NULL, NULL,
                 N'synthetic-before-up', SYSUTCDATETIME());
            INSERT admin.AuthAudits (Id, EventType, AdminUserId, Subject, Reason, CorrelationId, OccurredAt)
            VALUES
                (@authAuditId, N'login-success', @legacyId, NULL, NULL, N'synthetic-auth-before-up', SYSUTCDATETIME());
            UPDATE cfg.Offices SET LegacyKey = N'SYN-OFFICE' WHERE Id = @officeId;
            UPDATE cfg.Divisions SET LegacyKey = N'SYN-DIVISION' WHERE Id = @divisionId;
            CREATE TABLE cfg.VibEmp (EmpCode nvarchar(50) NULL, Marker nvarchar(32) NULL);
            CREATE TABLE cfg.branch (br_code char(3) NULL, Marker nvarchar(32) NULL);
            INSERT cfg.VibEmp (EmpCode, Marker) VALUES (N'SYNTH001', N'preserve-vibemp');
            INSERT cfg.branch (br_code, Marker) VALUES ('Z01', N'preserve-branch');
            """,
            ("@legacyId", legacyId), ("@positionId", PositionId), ("@officeId", OfficeId),
            ("@levelId", LevelId), ("@divisionId", DivisionId), ("@merchantId", Guid.NewGuid()),
            ("@sessionId", sessionId), ("@userAuditId", userAuditId), ("@authAuditId", authAuditId));

        await database.MigrateAsync(CurrentMigration);

        await using var connection = await database.OpenAsync();
        Assert.Equal(" Legacy@VIRIYAH.CO.TH ", Convert.ToString(await ScalarAsync(
            connection, "SELECT Email FROM admin.Users WHERE Id = @id;", ("@id", legacyId))));
        Assert.Equal(DBNull.Value, await ScalarAsync(
            connection, "SELECT TenantId FROM admin.Users WHERE Id = @id;", ("@id", legacyId)));
        Assert.Equal($"microsoft|{legacySubject}|2|2|9|7|SYNTH001|Alpha|Fixture|{PositionId:D}|{OfficeId:D}|{LevelId:D}|{DivisionId:D}",
            Convert.ToString(await ScalarAsync(connection,
                """
                SELECT CONCAT(Provider, '|', Subject, '|', Tier, '|', Status, '|', Version, '|',
                              AuthorizationVersion, '|', EmployeeId, '|', FirstName, '|', LastName, '|',
                              LOWER(CONVERT(nvarchar(36), PositionId)), '|', LOWER(CONVERT(nvarchar(36), OfficeId)), '|',
                              LOWER(CONVERT(nvarchar(36), LevelId)), '|', LOWER(CONVERT(nvarchar(36), DivisionId)))
                FROM admin.Users WHERE Id = @id;
                """, ("@id", legacyId))));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM admin.RoleAssignments WHERE AdminUserId = @id;", ("@id", legacyId))));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM admin.MerchantAccess WHERE AdminUserId = @id;", ("@id", legacyId))));
        Assert.Equal(legacyId, await ScalarAsync(connection,
            "SELECT AdminUserId FROM admin.Sessions WHERE Id = @id;", ("@id", sessionId)));
        Assert.Equal("synthetic-before-up", Convert.ToString(await ScalarAsync(connection,
            "SELECT Action FROM admin.UserAudits WHERE Id = @id;", ("@id", userAuditId))));
        Assert.Equal("login-success", Convert.ToString(await ScalarAsync(connection,
            "SELECT EventType FROM admin.AuthAudits WHERE Id = @id;", ("@id", authAuditId))));
        Assert.Equal("preserve-vibemp", Convert.ToString(await ScalarAsync(connection,
            "SELECT Marker FROM cfg.VibEmp WHERE EmpCode = N'SYNTH001';")));
        Assert.Equal("preserve-branch", Convert.ToString(await ScalarAsync(connection,
            "SELECT Marker FROM cfg.branch WHERE br_code = 'Z01';")));
        Assert.Equal("SYN-OFFICE", Convert.ToString(await ScalarAsync(connection,
            "SELECT LegacyKey FROM cfg.Offices WHERE Id = @id;", ("@id", OfficeId))));
        Assert.Equal("SYN-DIVISION", Convert.ToString(await ScalarAsync(connection,
            "SELECT LegacyKey FROM cfg.Divisions WHERE Id = @id;", ("@id", DivisionId))));
        Assert.Equal(DBNull.Value, await ScalarAsync(
            connection, "SELECT COL_LENGTH(N'admin.Users', N'WorkforceEmailKey');"));
        Assert.Equal("nvarchar|640|1", Convert.ToString(await ScalarAsync(connection, """
            SELECT CONCAT(t.name, '|', c.max_length, '|', c.is_nullable)
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'admin.Users') AND c.name = N'Email';
            """)));
        Assert.Equal("uniqueidentifier|1", Convert.ToString(await ScalarAsync(connection, """
            SELECT CONCAT(t.name, '|', c.is_nullable)
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'admin.Users') AND c.name = N'TenantId';
            """)));
        Assert.Equal("Provider,TenantId,Subject", Convert.ToString(await ScalarAsync(connection, """
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'admin.Users')
              AND i.name = N'IX_Users_Provider_TenantId_Subject' AND ic.key_ordinal > 0;
            """)));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, """
            SELECT is_unique FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'admin.Users') AND name = N'IX_Users_Provider_TenantId_Subject';
            """)));
        Assert.Contains("Subject", Convert.ToString(await ScalarAsync(connection, """
            SELECT filter_definition FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'admin.Users') AND name = N'IX_Users_Provider_TenantId_Subject';
            """)), StringComparison.Ordinal);
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.key_constraints
            WHERE name = N'AK_WorkforceTenantBindings_TenantId'
              AND parent_object_id = OBJECT_ID(N'admin.WorkforceTenantBindings') AND type = N'UQ';
            """)));
        Assert.Equal("TenantId|TenantId|0", Convert.ToString(await ScalarAsync(connection, """
            SELECT CONCAT(parentColumn.name, '|', principalColumn.name, '|', fk.delete_referential_action)
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns parentColumn
              ON parentColumn.object_id = fkc.parent_object_id AND parentColumn.column_id = fkc.parent_column_id
            JOIN sys.columns principalColumn
              ON principalColumn.object_id = fkc.referenced_object_id AND principalColumn.column_id = fkc.referenced_column_id
            WHERE fk.name = N'FK_Users_WorkforceTenantBindings_TenantId'
              AND fk.parent_object_id = OBJECT_ID(N'admin.Users');
            """)));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'admin.Users') AND name = N'IX_Users_TenantId' AND is_unique = 0;
            """)));
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'admin.Users')
              AND name IN (N'IX_Users_Email', N'IX_Users_WorkforceEmailKey', N'IX_Users_Provider_Subject');
            """)));
        Assert.Contains("Latin1_General_100_BIN2", Convert.ToString(await ScalarAsync(connection, """
            SELECT definition FROM sys.check_constraints
            WHERE name = N'CK_Users_TenantId_MicrosoftProvider'
              AND parent_object_id = OBJECT_ID(N'admin.Users');
            """)), StringComparison.Ordinal);
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'admin.Users') AND name = N'IX_Users_EmployeeId'
              AND is_unique = 1 AND filter_definition IS NOT NULL;
            """)));
        Assert.Equal("0|0|0|", Convert.ToString(await ScalarAsync(connection, """
            SELECT CONCAT(SnapshotCount, '|', MappedCount, '|', NoOpCount, '|', CompletedAt)
            FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1;
            """)));

        // Email is nullable/non-unique contact data.
        await ExecAsync(connection, """
            INSERT admin.Users (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@a, N'google', N'google-two', N'same@example.com', 1, 1, 0, 1, SYSUTCDATETIME()),
                   (@b, N'google', N'google-three', NULL, 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@a", Guid.NewGuid()), ("@b", Guid.NewGuid()));

        var tenantId = Guid.NewGuid();
        await ExecAsync(connection,
            "INSERT admin.WorkforceTenantBindings (Id, TenantId) VALUES (1, @tenantId);", ("@tenantId", tenantId));
        await ExecAsync(connection,
            "ALTER TABLE admin.WorkforceTenantBindings NOCHECK CONSTRAINT CK_WorkforceTenantBindings_Singleton;");
        var duplicateTenant = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(connection,
            "INSERT admin.WorkforceTenantBindings (Id, TenantId) VALUES (2, @tenantId);", ("@tenantId", tenantId)));
        Assert.Contains(duplicateTenant.Number, new[] { 2601, 2627 });
        await ExecAsync(connection,
            "ALTER TABLE admin.WorkforceTenantBindings WITH CHECK CHECK CONSTRAINT CK_WorkforceTenantBindings_Singleton;");
        var objectId = Guid.NewGuid().ToString("D");
        await ExecAsync(connection, """
            INSERT admin.Users (Id, Provider, TenantId, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@id, N'microsoft', @tenantId, @subject, N'shared@example.com', 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid()), ("@tenantId", tenantId), ("@subject", objectId));
        await ExecAsync(connection, """
            INSERT admin.Users (Id, Provider, TenantId, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@id, N'microsoft', @tenantId, @subject, N'shared@example.com', 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid()), ("@tenantId", tenantId), ("@subject", Guid.NewGuid().ToString("D")));
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(connection, """
            INSERT admin.Users (Id, Provider, TenantId, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@id, N'microsoft', @tenantId, @subject, NULL, 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid()), ("@tenantId", tenantId), ("@subject", objectId)));
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(connection, """
            INSERT admin.Users (Id, Provider, TenantId, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@id, N'microsoft', @tenantId, @subject, NULL, 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid()), ("@tenantId", Guid.NewGuid()), ("@subject", Guid.NewGuid().ToString("D"))));
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(connection, """
            INSERT admin.Users (Id, Provider, TenantId, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@id, N'google', @tenantId, N'bad', NULL, 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid()), ("@tenantId", tenantId)));
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(connection, """
            INSERT admin.Users (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@id, N'google', N'google-subject', NULL, 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid())));
        await ExecAsync(connection, """
            INSERT admin.Users (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@first, N'google', NULL, NULL, 1, 1, 0, 1, SYSUTCDATETIME()),
                   (@second, N'google', NULL, NULL, 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@first", Guid.NewGuid()), ("@second", Guid.NewGuid()));
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM admin.Users WHERE TenantId = @tenantId AND Email = N'shared@example.com';",
            ("@tenantId", tenantId))));
        Assert.True(Convert.ToInt32(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM admin.Users WHERE Subject IS NULL;")) >= 2);

        // Employee-profile columns/FKs survive byte-for-byte shape-wise.
        Assert.Equal(3, Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'admin.Users') AND name IN (N'EmployeeId', N'FirstName', N'LastName');
            """)));
        Assert.Equal(4, Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'admin.Users')
              AND name IN (N'FK_Users_Positions_PositionId', N'FK_Users_Offices_OfficeId',
                           N'FK_Users_Levels_LevelId', N'FK_Users_Divisions_DivisionId');
            """)));
    }

    [Fact]
    public async Task Completed_historical_state_with_unicode_edge_trim_round_trips_the_old_key_only_on_safe_down()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeHistoricalMigration);
        var adminId = Guid.NewGuid();
        var legacySubject = Guid.NewGuid().ToString("D");
        await database.InsertUserAsync(
            adminId, "microsoft", legacySubject, "\u00A0Owner@VIRIYAH.CO.TH\u00A0");
        await database.MigrateAsync(PreviousMigration);
        var unrelatedId = Guid.NewGuid();
        await database.InsertUserAsync(unrelatedId, "google", "google-subject-stays", "unrelated@example.com");
        await database.ExecuteAsync("""
            UPDATE admin.Users
            SET Subject = N'owner@viriyah.co.th', WorkforceEmailKey = N'owner@viriyah.co.th',
                EmployeeId = N'SYNTH-DOWN', FirstName = N'Before', LastName = N'Rollback',
                PositionId = @positionId, OfficeId = @officeId, LevelId = @levelId, DivisionId = @divisionId,
                Version = 12, AuthorizationVersion = 4
            WHERE Id = @id;
            UPDATE admin.WorkforceIdentitySubjectRollback
            SET CanonicalSubject = N'owner@viriyah.co.th', ConversionKind = N'converted'
            WHERE AdminUserId = @id;
            UPDATE admin.WorkforceIdentityMigrations
            SET CompletedAt = SYSUTCDATETIME(), ConvertedCount = 1, NoOpCount = 0
            WHERE Id = 1 AND SnapshotCount = 1;
            """, ("@id", adminId), ("@positionId", PositionId), ("@officeId", OfficeId),
            ("@levelId", LevelId), ("@divisionId", DivisionId));

        await database.MigrateAsync(CurrentMigration);
        await database.MigrateAsync(PreviousMigration);

        await using var verify = await database.OpenAsync();
        Assert.Equal("owner@viriyah.co.th", Convert.ToString(await ScalarAsync(
            verify, "SELECT WorkforceEmailKey FROM admin.Users WHERE Id = @id;", ("@id", adminId))));
        Assert.Equal("\u00A0Owner@VIRIYAH.CO.TH\u00A0", Convert.ToString(await ScalarAsync(
            verify, "SELECT Email FROM admin.Users WHERE Id = @id;", ("@id", adminId))));
        Assert.Equal("owner@viriyah.co.th", Convert.ToString(await ScalarAsync(
            verify, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", adminId))));
        Assert.Equal($"SYNTH-DOWN|Before|Rollback|{PositionId:D}|{OfficeId:D}|{LevelId:D}|{DivisionId:D}|12|4",
            Convert.ToString(await ScalarAsync(verify,
                """
                SELECT CONCAT(EmployeeId, '|', FirstName, '|', LastName, '|',
                              LOWER(CONVERT(nvarchar(36), PositionId)), '|', LOWER(CONVERT(nvarchar(36), OfficeId)), '|',
                              LOWER(CONVERT(nvarchar(36), LevelId)), '|', LOWER(CONVERT(nvarchar(36), DivisionId)), '|',
                              Version, '|', AuthorizationVersion)
                FROM admin.Users WHERE Id = @id;
                """, ("@id", adminId))));
        Assert.Equal("google|google-subject-stays|unrelated@example.com", Convert.ToString(await ScalarAsync(
            verify, "SELECT CONCAT(Provider, '|', Subject, '|', Email) FROM admin.Users WHERE Id = @id;",
            ("@id", unrelatedId))));
        Assert.Equal(4, Convert.ToInt32(await ScalarAsync(verify, """
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'admin.Users')
              AND name IN (N'FK_Users_Positions_PositionId', N'FK_Users_Offices_OfficeId',
                           N'FK_Users_Levels_LevelId', N'FK_Users_Divisions_DivisionId');
            """)));
    }

    [Fact]
    public async Task Up_aborts_before_ddl_when_completed_historical_key_differs_from_legacy_email()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeHistoricalMigration);
        var adminId = Guid.NewGuid();
        await database.InsertUserAsync(
            adminId, "microsoft", Guid.NewGuid().ToString("D"), "Owner@VIRIYAH.CO.TH");
        await database.MigrateAsync(PreviousMigration);
        await database.ExecuteAsync(
            """
            UPDATE admin.Users
            SET Subject = N'owner@viriyah.co.th', WorkforceEmailKey = N'other@viriyah.co.th'
            WHERE Id = @id;
            UPDATE admin.WorkforceIdentitySubjectRollback
            SET CanonicalSubject = N'owner@viriyah.co.th', ConversionKind = N'converted'
            WHERE AdminUserId = @id;
            UPDATE admin.WorkforceIdentityMigrations
            SET CompletedAt = SYSUTCDATETIME(), ConvertedCount = 1, NoOpCount = 0
            WHERE Id = 1 AND SnapshotCount = 1;
            """, ("@id", adminId));

        await Assert.ThrowsAnyAsync<Exception>(() => database.MigrateAsync(CurrentMigration));

        await using var verify = await database.OpenAsync();
        Assert.Equal(DBNull.Value, await ScalarAsync(verify, "SELECT COL_LENGTH(N'admin.Users', N'TenantId');"));
        Assert.NotEqual(DBNull.Value, await ScalarAsync(
            verify, "SELECT COL_LENGTH(N'admin.Users', N'WorkforceEmailKey');"));
        Assert.Equal("other@viriyah.co.th", Convert.ToString(await ScalarAsync(
            verify, "SELECT WorkforceEmailKey FROM admin.Users WHERE Id = @id;", ("@id", adminId))));
        Assert.Equal(DBNull.Value, await ScalarAsync(
            verify, "SELECT OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations', N'U');"));
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260902133906_Tier0MicrosoftTenantAwareIdentity';")));
    }

    [Fact]
    public async Task Up_aborts_before_ddl_when_pending_historical_key_state_has_drifted()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeHistoricalMigration);
        await database.InsertUserAsync(Guid.NewGuid(), "google", "subject", "owner@viriyah.co.th");
        await database.MigrateAsync(PreviousMigration);
        await database.ExecuteAsync(
            "UPDATE admin.Users SET WorkforceEmailKey = N'owner@viriyah.co.th';");

        await Assert.ThrowsAnyAsync<Exception>(() => database.MigrateAsync(CurrentMigration));

        await using var verify = await database.OpenAsync();
        Assert.Equal(DBNull.Value, await ScalarAsync(verify, "SELECT COL_LENGTH(N'admin.Users', N'TenantId');"));
        Assert.NotEqual(DBNull.Value, await ScalarAsync(
            verify, "SELECT COL_LENGTH(N'admin.Users', N'WorkforceEmailKey');"));
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260902133906_Tier0MicrosoftTenantAwareIdentity';")));
    }

    [Theory]
    [InlineData("null-email")]
    [InlineData("duplicate-email")]
    [InlineData("mapped-count")]
    public async Task Down_aborts_before_ddl_when_current_state_cannot_restore_old_constraints(string scenario)
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(CurrentMigration);
        if (scenario == "null-email")
        {
            await database.ExecuteAsync("""
                INSERT admin.Users (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                VALUES (@id, N'google', N'subject-a', NULL, 1, 1, 0, 1, SYSUTCDATETIME());
                """, ("@id", Guid.NewGuid()));
        }
        else if (scenario == "duplicate-email")
        {
            await database.ExecuteAsync("""
                INSERT admin.Users (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                VALUES (@a, N'google', N'subject-a', N'duplicate@example.com', 1, 1, 0, 1, SYSUTCDATETIME()),
                       (@b, N'google', N'subject-b', N'duplicate@example.com', 1, 1, 0, 1, SYSUTCDATETIME());
                """, ("@a", Guid.NewGuid()), ("@b", Guid.NewGuid()));
        }
        else
        {
            var adminId = Guid.NewGuid();
            await database.ExecuteAsync("""
                INSERT admin.Users (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                VALUES (@id, N'google', N'subject-a', N'mapped@example.com', 1, 1, 0, 1, SYSUTCDATETIME());
                INSERT admin.WorkforceTenantIdentitySnapshot (AdminUserId) VALUES (@id);
                UPDATE admin.WorkforceTenantIdentityMigrations
                SET CompletedAt = SYSUTCDATETIME(), SnapshotCount = 1, MappedCount = 1, NoOpCount = 0
                WHERE Id = 1;
                """, ("@id", adminId));
        }

        await Assert.ThrowsAnyAsync<Exception>(() => database.MigrateAsync(PreviousMigration));

        await using var verify = await database.OpenAsync();
        Assert.NotEqual(DBNull.Value, await ScalarAsync(verify,
            "SELECT COL_LENGTH(N'admin.Users', N'TenantId');"));
        Assert.Equal(DBNull.Value, await ScalarAsync(verify,
            "SELECT COL_LENGTH(N'admin.Users', N'WorkforceEmailKey');"));
        Assert.NotEqual(DBNull.Value, await ScalarAsync(verify,
            "SELECT OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations', N'U');"));
    }

    [Fact]
    public async Task Down_is_safe_before_mapping_and_aborts_without_partial_ddl_after_tenant_mapping()
    {
        await using (var safe = await ScratchDatabase.CreateAsync())
        {
            await safe.MigrateAsync(CurrentMigration);
            await safe.MigrateAsync(PreviousMigration);
            await using var verify = await safe.OpenAsync();
            Assert.NotEqual(DBNull.Value, await ScalarAsync(
                verify, "SELECT COL_LENGTH(N'admin.Users', N'WorkforceEmailKey');"));
            Assert.Equal(DBNull.Value, await ScalarAsync(
                verify, "SELECT COL_LENGTH(N'admin.Users', N'TenantId');"));
            Assert.Equal(DBNull.Value, await ScalarAsync(
                verify, "SELECT OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations', N'U');"));
        }

        await using var unsafeDatabase = await ScratchDatabase.CreateAsync();
        await unsafeDatabase.MigrateAsync(CurrentMigration);
        var tenantId = Guid.NewGuid();
        await unsafeDatabase.ExecuteAsync(
            "INSERT admin.WorkforceTenantBindings (Id, TenantId) VALUES (1, @tenantId);", ("@tenantId", tenantId));
        await unsafeDatabase.ExecuteAsync("""
            INSERT admin.Users (Id, Provider, TenantId, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@id, N'microsoft', @tenantId, @subject, NULL, 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid()), ("@tenantId", tenantId), ("@subject", Guid.NewGuid().ToString("D")));

        await Assert.ThrowsAnyAsync<Exception>(() => unsafeDatabase.MigrateAsync(PreviousMigration));

        await using var after = await unsafeDatabase.OpenAsync();
        Assert.NotEqual(DBNull.Value, await ScalarAsync(after,
            "SELECT COL_LENGTH(N'admin.Users', N'TenantId');"));
        Assert.NotEqual(DBNull.Value, await ScalarAsync(after,
            "SELECT OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations', N'U');"));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(after,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260902133906_Tier0MicrosoftTenantAwareIdentity';")));
    }

    private static async Task<object?> ScalarAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private const string Prefix = "pol_tenant_identity_it_";
        private ScratchDatabase(string name) => Name = name;
        public string Name { get; }

        public static async Task<ScratchDatabase> CreateAsync()
        {
            var database = new ScratchDatabase(Prefix + Guid.NewGuid().ToString("N"));
            await using var master = await database.OpenAsync("master");
            await ExecAsync(master, $"EXEC(N'CREATE DATABASE [{database.Name}] COLLATE Thai_100_CI_AS');");
            await ExecAsync(master, $"ALTER DATABASE [{database.Name}] SET COMPATIBILITY_LEVEL = 170;");
            await using var connection = await database.OpenAsync();
            await ExecAsync(connection, "CREATE USER pol_app WITHOUT LOGIN;");
            return database;
        }

        public Task MigrateAsync(string migration)
        {
            var context = CreateContext();
            return MigrateAndDisposeAsync(context, migration);
        }

        private static async Task MigrateAndDisposeAsync(PolDbContext context, string migration)
        {
            await using (context)
                await context.GetService<IMigrator>().MigrateAsync(migration);
        }

        public async Task InsertUserAsync(Guid id, string provider, string? subject, string email)
        {
            await using var connection = await OpenAsync();
            await ExecAsync(connection, """
                INSERT admin.Users (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                VALUES (@id, @provider, @subject, @email, 1, 1, 0, 7, SYSUTCDATETIME());
                """, ("@id", id), ("@provider", provider), ("@subject", (object?)subject ?? DBNull.Value), ("@email", email));
        }

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await OpenAsync();
            await ExecAsync(connection, sql, parameters);
        }

        public async Task ExecuteBatchesAsync(string script)
        {
            await using var connection = await OpenAsync();
            foreach (var batch in SqlScripts.SplitBatches(script))
                await ExecAsync(connection, batch);
        }

        public async Task<SqlConnection> OpenAsync(string? database = null)
        {
            var connection = new SqlConnection(ConnectionString(database ?? Name));
            await connection.OpenAsync();
            return connection;
        }

        private PolDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(ConnectionString(Name), sql => sql.UseCompatibilityLevel(170)).Options;
            return new PolDbContext(options, ModuleAssemblies());
        }

        public async ValueTask DisposeAsync()
        {
            await using var master = await OpenAsync("master");
            await ExecAsync(master,
                $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
        }

        private static ModuleAssemblies ModuleAssemblies() => new([
            typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
            typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
            typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
            typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
            typeof(Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
            typeof(Admins.Infrastructure.AdminModuleRegistration).Assembly,
            typeof(Iam.Infrastructure.IamModuleRegistration).Assembly,
            typeof(Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
            typeof(Levels.Infrastructure.LevelsModuleRegistration).Assembly,
            typeof(Offices.Infrastructure.OfficesModuleRegistration).Assembly,
            typeof(Positions.Infrastructure.PositionsModuleRegistration).Assembly,
            typeof(Governance.Infrastructure.GovernanceModuleRegistration).Assembly,
            typeof(Notifications.Infrastructure.NotificationsModuleRegistration).Assembly,
        ]);

        private static string ConnectionString(string database) => new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("POL_SQL_SERVER") ?? "localhost,11433",
            InitialCatalog = database,
            UserID = "sa",
            Password = Environment.GetEnvironmentVariable("POL_SA_PASSWORD")
                ?? throw new InvalidOperationException("Integration tests need POL_SA_PASSWORD."),
            Encrypt = true,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString;
    }
}
